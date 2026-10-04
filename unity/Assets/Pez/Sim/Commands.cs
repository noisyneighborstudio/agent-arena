using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>
    /// The single command surface for every player: mouse input, the scripted AI, and LLMs over HTTP/MCP
    /// all go through Execute, so nobody has abilities the others lack.
    /// </summary>
    public static class Commands
    {
        public const string Help =
@"Commands (JSON objects with a ""type"" field):
  {""type"":""build"", ""structure"":KEY, ""x"":X, ""y"":Y}   place a structure (bottom-left tile). Omit x/y to auto-place near your base.
  {""type"":""train"", ""unit"":KEY, ""count"":N}            queue N units (1-10) at the barracks / war factory
      add ""structure_id"":ID to have them come out of that building (e.g. a forward barracks); otherwise your first one
  {""type"":""move"", ""units"":[IDS], ""x"":X, ""y"":Y}     move, ignoring enemies
  {""type"":""attack_move"", ""units"":[IDS], ""x"":X, ""y"":Y}  move, engaging enemies on the way
      add ""together"":true to move or attack_move so the group keeps the slowest member's pace and arrives as one (leaders wait, stragglers catch up; aircraft fly their own pace)
      add ""spread"":3 (tiles between units, 1-6; true = 3) to open the formation up against splash (artillery, heavy tanks)
      add ""waypoints"":[[x,y],...] to queue more points (x/y optional: the first waypoint is used), and ""loop"":true to patrol them
  {""type"":""set_retreat"", ""units"":[IDS], ""below_pct"":30}  units pull back to base on their own below that HP % (0 = off)
  {""type"":""attack"", ""units"":[IDS], ""target"":ID}     focus fire on a visible enemy
  {""type"":""stop"", ""units"":[IDS]}
  {""type"":""harvest"", ""units"":[IDS], ""x"":X, ""y"":Y}  send mining trucks to ore near x,y (they stick to that ore type)
  {""type"":""harvest"", ""units"":[IDS], ""ore"":""crystal""}  send mining trucks to the nearest ore of a type (iron_ore, copper_ore, crystal, uranium; ""any"" to reset)
  {""type"":""repair"", ""units"":[IDS], ""target"":ID}     repair trucks fix a damaged friendly vehicle, aircraft or structure (costs steel), and refuel vehicles (free)
  {""type"":""refuel"", ""units"":[IDS], ""target"":ID}     vehicles/aircraft go refuel (target optional: a pad or depot), then resume their order
  {""type"":""heal"", ""units"":[IDS], ""target"":ID}       medics heal a wounded friendly infantry unit (free); same as repair
  {""type"":""load"", ""units"":[INFANTRY IDS], ""transport"":ID}   infantry walk to an APC / transport_chopper and board it
  {""type"":""unload"", ""units"":[TRANSPORT IDS]}          drop all passengers where the transport is
  {""type"":""capture"", ""units"":[ENGINEER IDS], ""target"":ID}  engineer takes over an enemy structure below 50% HP, or a neutral derrick at any HP (engineer is used up); for a derrick, ""x"",""y"" of its site works instead of the id (ids change when one is rebuilt)
  {""type"":""lay_mines"", ""units"":[MINELAYER IDS], ""x"":X, ""y"":Y, ""count"":N}  lay up to 8 hidden mines around x,y (30 steel each)
  {""type"":""deploy"", ""units"":[IDS]}                   deploy an outpost_truck into an Outpost or a construction_truck into a Command Center where it stands, or a drill_rig into a Deep Mine on a surveyed deep deposit within 3 tiles
  {""type"":""survey"", ""units"":[SURVEYOR IDS], ""x"":X, ""y"":Y}  survey one spot for deep ore deposits (8s, 12-tile radius), flag each one found as a mining zone (only your team sees them), then wait
  {""type"":""prospect"", ""units"":[SURVEYOR IDS], ""x"":X, ""y"":Y, ""radius"":R}  surveyors roam on their own: survey, flag what they find, move on to the nearest unsurveyed spot, until nothing is left within R tiles of x,y (all optional: default 40 tiles around each surveyor) or you give another order
  {""type"":""drill"", ""units"":[DRILL RIG IDS], ""zone"":ID}  a drill_rig drives to that mining zone and deploys into a Deep Mine on arrival (omit zone: each rig takes the nearest free zone)
  {""type"":""rally"", ""structure_id"":ID, ""x"":X, ""y"":Y} where new units from that building go
  {""type"":""sell"", ""structure_id"":ID}                 sell for 50% refund (your only one of a tech or production building needs ""confirm"":true)
  {""type"":""reserve"", ""item"":""iron_ore"", ""amount"":300}  converters (refineries, plants) leave this much of an item alone, so raw-ore costs (power_plant, mining_refinery, mining_truck) stay payable; amount 0 clears it
  {""type"":""save_for"", ""unit"":""drill_rig""}           save up for one unit or structure: converters leave its cost alone until you train/build it (no unit: cancel)
  {""type"":""cancel"", ""unit"":KEY}                       cancel the last queued unit of that type (full refund)
  {""type"":""say"", ""text"":""...""}                      broadcast a chat message (shown on screen)
  {""type"":""propose_tech"", ""name"":""Lancer"", ""base"":""light_tank"", ""weapon_from"":""rocket_soldier"", ""hp"":360, ""damage"":70, ""dry_run"":true}
      design your own unit from one you can build (see inventions in the rules); dry_run quotes it, without dry_run it's researched, then train its key
'units' may also be the string ""all"" (all your combat units) or ""idle"" (idle combat units).";

        public static JObj Execute(World w, int team, Dictionary<string, object> c)
        {
            try
            {
                if (team < 0 || team >= w.Teams.Count) return Err("bad team");
                if (w.GameOver) return Err("game is over");
                if (w.Teams[team].Defeated) return Err("your team is defeated");
                var type = c.Str("type", "").ToLowerInvariant();
                // Protection stops you hurting other players; taking a neutral derrick hurts no one.
                if ((type == "attack" || (type == "capture" && !CapturesNeutral(w, c))) && w.IsProtected(team))
                    return Err($"you're under newcomer protection for {(int)(w.Teams[team].ProtectedUntil - w.Time)}s more and can't attack yet; use the time to build up");
                switch (type)
                {
                    case "build": return Build(w, team, c);
                    case "train": return Train(w, team, c);
                    case "move": return UnitOrder(w, team, c, Order.Move);
                    case "attack_move": return UnitOrder(w, team, c, Order.AttackMove);
                    case "attack": return Attack(w, team, c);
                    case "stop": return UnitOrder(w, team, c, Order.Idle);
                    case "harvest": return UnitOrder(w, team, c, Order.Harvest);
                    case "deploy": return Deploy(w, team, c);
                    case "repair":
                    case "heal": return Repair(w, team, c);
                    case "load": return Load(w, team, c);
                    case "unload": return Unload(w, team, c);
                    case "capture": return Capture(w, team, c);
                    case "lay_mines": return LayMines(w, team, c);
                    case "rally": return Rally(w, team, c);
                    case "refuel": return Refuel(w, team, c);
                    case "set_retreat": return SetRetreat(w, team, c);
                    case "survey": return Survey(w, team, c);
                    case "prospect": return Prospect(w, team, c);
                    case "drill": return Drill(w, team, c);
                    case "sell": return Sell(w, team, c);
                    case "reserve": return Reserve(w, team, c);
                    case "save_for": return SaveFor(w, team, c);
                    case "cancel": return Cancel(w, team, c);
                    case "propose_tech": return Tech.Propose(w, team, c);
                    case "say":
                        {
                            var text = Text.Clean(c.Str("text", ""), 200);
                            if (text.Length == 0) return Err("text is empty");
                            w.Emit("chat", team, text: text);
                            return Ok();
                        }
                    default: return Err($"unknown command type '{type}'");
                }
            }
            catch (Exception ex) { return Err(ex.Message); }
        }

        static JObj Ok(string msg = null) { var o = new JObj().Set("ok", true); if (msg != null) o.Set("result", msg); return o; }
        static JObj Err(string msg) => new JObj().Set("ok", false).Set("error", msg);

        static JObj Build(World w, int team, Dictionary<string, object> c)
        {
            var key = c.Str("structure") ?? c.Str("key");
            var def = Defs.Get(key);
            if (def == null || !def.IsStructure) return Err($"unknown structure '{key}'. Valid: {string.Join(", ", Defs.All.Values.Where(d => d.IsStructure && d.Buildable).Select(d => d.Key))}");
            if (!def.Buildable) return Err($"{key} can't be built from the menu" + (key == "outpost" ? "; train an outpost_truck at a factory and deploy it" : ""));
            var missing = w.MissingPrereq(team, def);
            if (missing != null) return Err($"{key} {missing}");
            var t = w.Teams[team];
            var lacking = t.Missing(def.Cost);
            if (lacking != null) return Err($"{key}: {lacking}{ReserveHint(w, team, def.Cost)}");
            float x = c.Num("x"), y = c.Num("y");
            Int2 origin;
            if (float.IsNaN(x) || float.IsNaN(y))
            {
                var spot = w.FindPlacement(team, key);
                if (!spot.HasValue) return Err("no valid spot found near your base; specify x,y");
                origin = spot.Value;
            }
            else
            {
                origin = new Int2((int)x, (int)y);
                var why = w.CanPlace(team, key, origin.X, origin.Y);
                if (why != null)
                {
                    var alt = w.FindPlacement(team, key, new Vec2(x, y));
                    return Err($"cannot place {key} at ({origin.X},{origin.Y}): {why}" + (alt.HasValue ? $". Nearest valid spot: ({alt.Value.X},{alt.Value.Y})" : ""));
                }
            }
            t.Pay(def.Cost);
            bool wasSaving = t.Bought(key);
            var s = w.SpawnStructure(team, key, origin, 0f);
            t.StructureQueue.Add(new ProdItem { Key = key, StructureId = s.Id });
            w.Emit("placed", team, s.Id, pos: s.Center, key: key);
            int ahead = t.StructureQueue.Count - 1;
            // Power, counting everything placed (finished or not): say now if this tips you into low power when it's done.
            string power = "";
            if (def.Power < 0)
            {
                int made = 0, drawn = 0;
                foreach (var o in w.Owned(team).Where(o => o.IsStructure)) { if (o.Def.Power > 0) made += o.Def.Power; else drawn -= o.Def.Power; }
                if (drawn > made) power = $". Power warning: with everything placed you'll draw {drawn} against {made} produced, which is LOW POWER (half speed); add a power_plant";
            }
            return Ok($"{key} #{s.Id} placed at ({origin.X},{origin.Y}), size {def.SizeX}x{def.SizeY}, build time {def.BuildTime}s" + (ahead > 0 ? $", {ahead} structure(s) ahead in queue" : "") + power +
                      (wasSaving ? " (that was what you were saving for: converters are back to normal)" : ""))
                .Set("id", s.Id);
        }

        static JObj Train(World w, int team, Dictionary<string, object> c)
        {
            var key = c.Str("unit") ?? c.Str("key");
            var def = w.Def(key);
            if (def == null || def.IsStructure) return Err($"unknown unit '{key}'. Valid: {string.Join(", ", Defs.All.Values.Where(d => !d.IsStructure).Select(d => d.Key).Concat(w.Inventions.Values.Where(i => i.Team == team).Select(i => i.Key)))}");
            var missing = w.MissingPrereq(team, def);
            if (missing != null) return Err($"{key} {missing}");
            float asked = c.Num("count", 1);
            if (float.IsNaN(asked) || asked < 1 || asked > 10) return Err("count must be 1 to 10 (queue more with another train)");
            int count = Math.Min(10, (int)asked);
            var t = w.Teams[team];
            Entity at = null;
            if (c.ContainsKey("structure_id"))
            {
                at = OwnStructure(w, team, c);
                var producers = w.Owned(team).Where(s => s.IsStructure && s.IsComplete && s.Def.Produces == def.BuiltBy).Select(s => $"#{s.Id} at {s.Origin.X},{s.Origin.Y}").ToList();
                if (at == null || at.Def.Produces != def.BuiltBy || !at.IsComplete)
                    return Err($"structure_id must be one of your completed {Defs.ProducerKey(def.BuiltBy)} buildings to train {key}: {(producers.Count > 0 ? string.Join(", ", producers) : "you have none")}");
            }
            int queued = 0;
            for (int i = 0; i < count; i++)
            {
                if (t.Missing(def.Cost) != null) break;
                t.Pay(def.Cost);
                t.UnitQueues[def.BuiltBy].Add(new ProdItem { Key = key, StructureId = at?.Id ?? 0 });
                queued++;
            }
            string saved = queued > 0 && t.Bought(key) ? $" (that was what you were saving for: converters are back to normal)" : "";
            if (queued == 0) return Err($"{key}: {t.Missing(def.Cost)}{ReserveHint(w, team, def.Cost)}");
            return Ok($"queued {queued}x {key}" + (at != null ? $" at {at.Def.Key} #{at.Id}" : "") + (queued < count ? $" (could only afford {queued})" : "") + $"; {t.UnitQueues[def.BuiltBy].Count} in the queue shared by your {Defs.ProducerKey(def.BuiltBy)} buildings" + saved);
        }

        static List<Entity> ResolveUnits(World w, int team, Dictionary<string, object> c)
        {
            if (c.TryGetValue("units", out var v) && v is string s)
            {
                var mine = w.Owned(team).Where(e => !e.IsStructure && e.IsArmed);
                if (s == "idle") mine = mine.Where(e => e.Order == Order.Idle);
                return mine.ToList();
            }
            return c.Ids("units").Select(w.Get).Where(e => e != null && e.Team == team && !e.IsStructure && !e.IsMine && !e.IsCarried).ToList();
        }

        static JObj UnitOrder(World w, int team, Dictionary<string, object> c, Order order)
        {
            var units = ResolveUnits(w, team, c);
            if (units.Count == 0) return Err("no valid units of yours given (use ids from your unit list)");
            float x = c.Num("x"), y = c.Num("y");
            // Queued waypoints: "waypoints":[[x,y],...] (after x,y if given, else starting with the first).
            var waypoints = ParsePoints(c.TryGetValue("waypoints", out var wpRaw) ? wpRaw : null);
            if (waypoints == null) return Err("waypoints must be a list like [[10,20],[30,40]] or [{\"x\":10,\"y\":20}]");
            foreach (var p in waypoints) if (!w.Map.InBounds((int)p.X, (int)p.Y)) return Err($"waypoint ({p.X},{p.Y}) is outside the {w.Map.W}x{w.Map.H} map");
            if ((order == Order.Move || order == Order.AttackMove) && float.IsNaN(x) && waypoints.Count > 0) { x = waypoints[0].X; y = waypoints[0].Y; waypoints.RemoveAt(0); }
            bool loop = c.TryGetValue("loop", out var lp) && lp is bool lb && lb;
            if (order != Order.Idle && (float.IsNaN(x) || float.IsNaN(y)))
            {
                if (order != Order.Harvest) return Err("x and y are required");
            }
            var dest = new Vec2(float.IsNaN(x) ? 0 : x, float.IsNaN(y) ? 0 : y);
            if (order != Order.Idle && !float.IsNaN(x) && !w.Map.InBounds((int)x, (int)y)) return Err($"({x},{y}) is outside the {w.Map.W}x{w.Map.H} map");

            if (order == Order.Harvest)
            {
                var hs = units.Where(u => u.IsHarvester).ToList();
                if (hs.Count == 0) return Err("none of those units are mining trucks");
                var oreName = c.Str("ore");
                int type = oreName == null || oreName == "any" ? -1 : Array.IndexOf(Defs.Ores, oreName);
                if (oreName != null && oreName != "any" && type < 0) return Err($"unknown ore '{oreName}'. Valid: {string.Join(", ", Defs.Ores)}");
                var none = new List<Entity>();
                foreach (var h in hs)
                {
                    var tile = float.IsNaN(x) ? w.Map.NearestOre(h.Pos, 80, null, type) : w.Map.NearestOre(dest, 12, null, type);
                    if (tile == null) none.Add(h); // it stays on harvest and keeps looking (and raises SURFACE ORE RUNNING OUT)
                    w.SetOrder(h, Order.Harvest, tile?.Center ?? h.Pos);
                    if (oreName != null) h.HarvestType = type;
                }
                string what = type >= 0 ? Defs.Ores[type] : "ore";
                string where = float.IsNaN(x) ? "within 80 tiles of " + (none.Count == 1 ? "it" : "them") : $"within 12 tiles of {(int)x},{(int)y}";
                string why = $"no surface {what} {where} (it may be mined out): scout for more, give x,y of a field you know, or survey for deep deposits and drill them";
                // Where they're going, if it's risky: far from every drop-off, or by an enemy base you know of.
                var risks = new List<string>();
                var drops = w.Owned(team).Where(o => o.IsStructure && o.Def.DropOff).ToList();
                var hostile = w.Teams[team].KnownEnemyStructures.Values.Where(k => k.team >= 0 && k.key != "derrick").ToList();
                foreach (var h in hs.Where(h => !none.Contains(h)))
                {
                    var at = h.OrderPos;
                    float home = drops.Count == 0 ? 0 : drops.Min(o => o.DistFrom(at));
                    var foe = hostile.Where(k => Vec2.Dist(k.origin.Center, at) < World.HostileOreRadius).Select(k => k.key).FirstOrDefault();
                    // Enemy units you can see near the field count too (test player Kestrel lost 10 trucks to an army sitting on salvage).
                    int armed = w.Entities.Count(o => !o.Dead && o.Team != team && o.Team >= 0 && o.IsArmed && !o.IsStructure && Vec2.Dist(o.Pos, at) < World.HostileOreRadius && w.IsVisibleTo(team, o));
                    if (foe != null) risks.Add($"#{h.Id} is heading to {(int)at.X},{(int)at.Y}, by an enemy {foe} you know of");
                    else if (armed > 0) risks.Add($"#{h.Id} is heading to {(int)at.X},{(int)at.Y}, where you can see {armed} armed enemy unit(s)");
                    else if (home > World.FarFieldDist) risks.Add($"#{h.Id} is heading to {(int)at.X},{(int)at.Y}, {home:0} tiles from your nearest drop-off");
                }
                string risk = risks.Count == 0 ? "" : $". Careful: {string.Join("; ", risks.Take(4))}{(risks.Count > 4 ? $" (+{risks.Count - 4} more)" : "")}: escort them, or build a refinery or outpost by that field";
                if (none.Count == 0) return Ok($"{hs.Count} truck(s) mining" + (type >= 0 ? $" {Defs.Ores[type]}" : "") + risk);
                return Ok($"{hs.Count - none.Count} of {hs.Count} truck(s) found {what} to mine; {string.Join(", ", none.Select(h => "#" + h.Id))} found none and will sit idle: {why}" + risk);
            }

            // Spread a group around the destination so they don't all fight for one tile ("spread" opens the grid up).
            float gap = 0.9f;
            if (c.TryGetValue("spread", out var sp))
            {
                if (sp is bool sb) gap = sb ? 3f : 0.9f;
                else if (sp is double sd && sd >= 1 && sd <= 6) gap = (float)sd;
                else return Err("spread must be true or the tiles between units, 1-6");
            }
            int n = units.Count, i = 0;
            int cols = (int)MathF.Ceiling(MathF.Sqrt(n));
            foreach (var u in units)
            {
                var off = new Vec2((i % cols - (cols - 1) / 2f) * gap, (i / cols - (cols - 1) / 2f) * gap);
                i++;
                var target = n > 1 && order != Order.Idle ? dest + off : dest;
                if (u.IsHarvester && order == Order.AttackMove) { w.SetOrder(u, Order.Move, target); continue; }
                w.SetOrder(u, order, order == Order.Idle ? u.Pos : target);
                if (order == Order.Move || order == Order.AttackMove)
                {
                    foreach (var p in waypoints) u.Waypoints.Add(n > 1 ? p + off : p);
                    u.WaypointLoop = loop && waypoints.Count > 0;
                }
            }
            // Moving together: the group keeps to the slowest member's pace, the leaders wait for the rest and anyone left
            // behind catches up at full speed, so it arrives as one. Aircraft fly their own pace (they burn fuel by the second).
            var ground = units.Where(u => !u.IsAir).ToList();
            bool together = order != Order.Idle && ground.Count > 1 && c.TryGetValue("together", out var tg) && tg is bool tb && tb;
            float pace = together ? ground.Min(u => u.Def.Speed) : 0;
            if (together) { int group = ground.Min(u => u.Id); foreach (var u in ground) { u.SpeedCap = pace; u.Group = group; } }
            var low = units.Where(u => u.Def.UsesFuel && u.FuelFraction < 0.35f).ToList();
            var shortOfFuel = order == Order.Idle ? new List<string>() : FuelShortfalls(w, units, dest, waypoints);
            return Ok($"{n} unit(s) {(order == Order.Idle ? "stopped" : order == Order.AttackMove ? "attack-moving" : "moving")}" +
                      (together ? $" together at {pace:0.0} tiles/s{(ground.Count < n ? " (aircraft at their own speed)" : "")}" : "") +
                      (waypoints.Count > 0 ? $", then {waypoints.Count} more waypoint(s){(loop ? " on a loop" : "")}" : "") +
                      (shortOfFuel.Count > 0 ? $"; FUEL: {string.Join("; ", shortOfFuel.Take(4))}{(shortOfFuel.Count > 4 ? $" (+{shortOfFuel.Count - 4} more)" : "")}. " +
                                               "Units turn back to refuel when what's left only just gets them to a refuel point: deploy an outpost along the way, or send a repair truck with them"
                       : low.Count > 0 ? $"; low fuel: {string.Join(", ", low.Select(u => $"#{u.Id} {StateView.Pct(u.FuelFraction)}%"))} (they'll turn back to refuel when they must)" : ""));
        }

        /// <summary>Roads wind: a ground route is about this much longer than the straight line (the bingo-fuel rule assumes the same).</summary>
        const float RouteFactor = 1.4f;

        /// <summary>
        /// Units that can't drive the whole route and still get back to a refuel point from the far end: they'd turn
        /// back partway. A repair truck travelling with them refuels them in the field, so it covers ground units.
        /// </summary>
        static List<string> FuelShortfalls(World w, List<Entity> units, Vec2 dest, List<Vec2> waypoints)
        {
            var result = new List<string>();
            bool tankerAlong = units.Any(World.IsTanker);
            foreach (var u in units.Where(u => u.Def.UsesFuel && !u.IsCarried))
            {
                if (tankerAlong && !u.IsAir && !World.IsTanker(u)) continue;
                // Walk the route with the sim's own bingo rule: the unit turns back where the fuel left only just gets it to
                // the nearest refuel point with its reserve (vehicles only start checking below half a tank).
                float raw = World.FuelTiles(u, u.Fuel), burn = World.BurnPerTile(u), reach = raw / burn; // straight-line tiles the tank buys
                float half = World.FuelTiles(u, u.FuelMax * 0.5f);
                var pads = w.Entities.Where(s => World.IsFuelPoint(u, s) && !units.Contains(s)).Select(s => s.Center).ToList();
                float PadDist(Vec2 p) => pads.Count == 0 ? 0 : pads.Min(q => Vec2.Dist(p, q));
                var legs = new List<Vec2> { u.Pos, dest };
                legs.AddRange(waypoints);
                float route = 0, traveled = 0; Vec2? turn = null;
                for (int k = 1; k < legs.Count; k++) route += Vec2.Dist(legs[k - 1], legs[k]);
                for (int k = 1; k < legs.Count && turn == null; k++)
                {
                    float len = Vec2.Dist(legs[k - 1], legs[k]);
                    for (float d = 0; d <= len; d += 2f)
                    {
                        var p = legs[k - 1] + (legs[k] - legs[k - 1]) * (len > 0 ? d / len : 0);
                        float left = raw - (traveled + d) * burn;
                        if (pads.Count > 0 && left <= World.BingoTiles(u, PadDist(p)) && (u.IsAir || left <= half) && traveled + d > 0) { turn = p; traveled += d; break; }
                        if (left <= 0) { turn = p; traveled += d; break; }
                    }
                    if (turn == null) traveled += len;
                }
                if (turn == null && raw - route * burn > (pads.Count > 0 ? World.BingoTiles(u, PadDist(legs[legs.Count - 1])) : 0)) continue;
                var at = turn ?? legs[legs.Count - 1];
                // Gets (nearly) all the way: the trip is fine, the way back to fuel isn't. Say so plainly, not as a turn-back.
                if (pads.Count > 0 && MathF.Min(traveled, route) >= route - 4f)
                {
                    result.Add($"#{u.Id} {u.Def.Key} reaches the end of its ~{route:0}-tile trip with ~{MathF.Max(0, (raw - route * burn) / burn):0} tiles of fuel left, " +
                               $"short of the ~{PadDist(legs[legs.Count - 1]):0} back to the nearest refuel point (it heads back for fuel on arrival)");
                    continue;
                }
                result.Add(pads.Count == 0
                    ? $"#{u.Id} {u.Def.Key} has ~{reach:0} tiles of fuel for a ~{route:0}-tile trip and nowhere to refuel"
                    : $"#{u.Id} {u.Def.Key} has ~{reach:0} tiles of fuel for a ~{route:0}-tile trip; it turns back to refuel about {MathF.Min(traveled, route):0} tiles out, near {(int)at.X},{(int)at.Y}");
            }
            return result;
        }

        static JObj Attack(World w, int team, Dictionary<string, object> c)
        {
            var units = ResolveUnits(w, team, c).Where(u => u.IsArmed).ToList();
            if (units.Count == 0) return Err("no valid armed units of yours given");
            var target = w.Get((int)c.Num("target", 0));
            if (target == null) return Err($"nothing with id #{(int)c.Num("target", 0)} exists: it was destroyed, or the id is wrong");
            if (target.Team == team) return Err("that is your own unit");
            if (target.Team < 0) return Err($"{target.Def.Key} #{target.Id} is neutral: it can't be hurt; capture it with an engineer instead");
            if (w.IsProtected(target.Team)) return Err($"that player is under newcomer protection for {(int)(w.Teams[target.Team].ProtectedUntil - w.Time)}s more");
            if (!w.IsVisibleTo(team, target)) return Err("target is not currently visible; use attack_move toward its last known position");
            var able = units.Where(u => u.Def.Weapon.CanHit(target.Def)).ToList();
            if (able.Count == 0) return Err($"none of those units can hit a {(target.IsAir ? "flying" : "ground")} {target.Def.Key}");
            foreach (var u in able) w.SetOrder(u, Order.Attack, target.Center, target.Id);
            return Ok($"{able.Count} unit(s) attacking {target.Def.Key} #{target.Id}" + (able.Count < units.Count ? $" ({units.Count - able.Count} can't hit it)" : ""));
        }

        static Entity OwnStructure(World w, int team, Dictionary<string, object> c)
        {
            var s = w.Get((int)c.Num("structure_id", c.Num("id", 0)));
            return s != null && s.Team == team && s.IsStructure ? s : null;
        }

        static JObj Rally(World w, int team, Dictionary<string, object> c)
        {
            var s = OwnStructure(w, team, c);
            if (s == null) return Err("structure_id must be one of your structures");
            float x = c.Num("x"), y = c.Num("y");
            if (float.IsNaN(x) || float.IsNaN(y)) return Err("x and y are required");
            // Rally applies to every producer of the same kind, which is what players expect.
            foreach (var o in w.Owned(team).Where(o => o.IsStructure && o.Def.Produces == s.Def.Produces)) o.Rally = new Vec2(x, y);
            // New units drive there on their own tank: say what the trip costs the shortest-legged one this building makes.
            float dist = Vec2.Dist(s.Center, new Vec2(x, y));
            float Tank(EntityDef d) => d.RangeMaps > 0 ? w.DroneFuel(d) : d.Fuel; // drones' tanks are sized to the map
            var worst = Defs.All.Values.Where(d => d.BuiltBy == s.Def.Produces && d.UsesFuel && !d.IsStructure)
                                       .OrderByDescending(d => dist * (d.IsAir ? 1.15f : RouteFactor) / (Tank(d) * d.Speed)).FirstOrDefault();
            float used = worst == null ? 0 : dist * (worst.IsAir ? 1.15f : RouteFactor) / (Tank(worst) * worst.Speed);
            return Ok($"rally point for {s.Def.Key} set to ({x},{y})" +
                      (used >= 0.25f ? $"; it's {dist:0} tiles away: a new {worst.Key} burns ~{StateView.Pct(MathF.Min(1, used))}% of its fuel getting there" +
                                       (used >= 0.5f ? " and may turn back to refuel before it arrives; rally nearer, or at a refuel point (outpost)" : "") : ""));
        }

        static JObj Sell(World w, int team, Dictionary<string, object> c)
        {
            var s = OwnStructure(w, team, c);
            if (s == null) return Err("structure_id must be one of your structures");
            // Selling the last of a tech building locks out what needs it: ask first (it's easy to sell your own tech gate).
            var lost = w.Owned(team).Any(o => o != s && o.IsStructure && o.Def.Key == s.Def.Key) ? new List<string>()
                : Defs.All.Values.Where(d => d.Buildable && d.OwnerTeam < 0 && (d.Requires.Contains(s.Def.Key) || Defs.ProducerKey(d.BuiltBy) == s.Def.Key)).Select(d => d.Key).ToList();
            string lockout = lost.Count == 0 ? "" : $"{string.Join(", ", lost.Take(8))}{(lost.Count > 8 ? $" and {lost.Count - 8} more" : "")} can't be built until you have one again";
            if (lost.Count > 0 && !(c.TryGetValue("confirm", out var cf) && cf is bool yes && yes))
                return Err($"{s.Def.Key} #{s.Id} is your only {s.Def.Key}: without it {lockout}. Send the sell again with \"confirm\":true to sell it anyway");
            // Complete: half back, scaled by health. Unfinished: unbuilt share back in full.
            float factor = s.IsComplete ? 0.5f * (s.Hp / s.Def.MaxHp) : 1f - 0.5f * s.BuildProgress;
            w.Teams[team].Pay(s.Def.Cost, -factor);
            string refund = string.Join(", ", s.Def.Cost.Select(kv => $"{(int)(kv.Value * factor)} {kv.Key}"));
            w.Emit("sold", team, s.Id, pos: s.Center, key: s.Def.Key);
            w.Emit("destroyed", team, s.Id, 0, s.Center, key: s.Def.Key);
            w.Remove(s);
            string locked = lost.Count > 0 ? $". That was your only {s.Def.Key}: {lockout}" : "";
            // Aircraft that just lost their last place to land: say so now, not when they crash.
            var grounded = w.Owned(team).Where(u => u.IsAir && u.Def.UsesFuel && !w.Owned(team).Any(p => World.IsFuelPoint(u, p))).ToList();
            if (grounded.Count > 0) locked += $". Warning: {string.Join(", ", grounded.Take(6).Select(u => $"{u.Def.Key} #{u.Id}"))} now {(grounded.Count == 1 ? "has" : "have")} nowhere to refuel and will crash when the fuel runs out: build an airfield (or a factory, for drones)";
            if (refund.Length > 0) return Ok($"sold {s.Def.Key} for {refund}{locked}");
            string power = s.Def.Power < 0 ? $"; {-s.Def.Power} power freed" : s.Def.Power > 0 ? $"; its {s.Def.Power} power output is gone" : "";
            return Ok($"removed {s.Def.Key} #{s.Id}: it cost nothing, so there's no refund{power}{locked}");
        }

        /// <summary>Save up for one unit or structure: converters leave its cost alone until you buy it (test player Reed
        /// juggled reserves by hand to afford an 800-steel drill rig while electronics plants ate the steel).</summary>
        static JObj SaveFor(World w, int team, Dictionary<string, object> c)
        {
            var t = w.Teams[team];
            var key = c.Str("unit") ?? c.Str("structure") ?? c.Str("key");
            if (string.IsNullOrEmpty(key))
            {
                string was = t.SaveFor; t.SaveFor = null; t.SaveForCost.Clear();
                return Ok(was == null ? "you weren't saving for anything" : $"stopped saving for {was}: converters are back to normal");
            }
            var def = w.Def(key);
            if (def == null) return Err($"unknown unit or structure '{key}'");
            t.SaveFor = key; t.SaveForCost.Clear();
            foreach (var kv in def.Cost) t.SaveForCost[kv.Key] = kv.Value;
            string have = string.Join(", ", def.Cost.Select(kv => $"{t.Amount(kv.Key)}/{kv.Value} {kv.Key}"));
            string ready = t.Missing(def.Cost) == null ? " You can afford it now." : "";
            return Ok($"saving for {key}: converters leave {string.Join(", ", def.Cost.Select(kv => $"{kv.Value} {kv.Key}"))} alone until you buy it (have {have}).{ready} " +
                      $"Then train or build it as usual and the hold lifts; {{\"type\":\"save_for\"}} with no unit cancels.");
        }

        static JObj Reserve(World w, int team, Dictionary<string, object> c)
        {
            var item = c.Str("item") ?? c.Str("ore");
            if (item == null || !Defs.Items.Contains(item)) return Err($"item must be one of: {string.Join(", ", Defs.Items)}");
            float amount = c.Num("amount");
            if (float.IsNaN(amount) || amount < 0 || amount > 100000) return Err("amount must be 0-100000 (0 clears the reserve)");
            var t = w.Teams[team];
            if (amount < 1) t.Reserve.Remove(item); else t.Reserve[item] = (int)amount;
            string now = t.Reserve.Count == 0 ? "no reserves" : "reserves: " + string.Join(", ", t.Reserve.Select(kv => $"{kv.Value} {kv.Key}"));
            // What that stops making: a reserve on iron_ore is a reserve on steel production too.
            var stops = Defs.All.Values.Where(d => d.IsStructure).SelectMany(d => d.Recipes).Where(r => r.Inputs.ContainsKey(item)).SelectMany(r => r.Outputs.Keys).Distinct().ToList();
            string stall = amount >= 1 && t.Amount(item) <= amount ? $". Note: you have only {t.Amount(item)}, so your converters won't use any {item} until you have more than {(int)amount}" +
                (stops.Count > 0 ? $": no {string.Join(" or ", stops)} gets made from it until then" : "") + " (building and training still spend it)" : "";
            return Ok((amount < 1 ? $"{item} reserve cleared" : $"converters now leave {(int)amount} {item} alone ({item} {t.Amount(item)} in stock now)") + stall + $"; {now}");
        }

        /// <summary>A raw cost the team can't pay because its own converters eat that item as it arrives: say how to keep some.</summary>
        static string ReserveHint(World w, int team, Dictionary<string, int> cost)
        {
            var t = w.Teams[team];
            var eaten = cost.Where(kv => t.Amount(kv.Key) < kv.Value && t.Reserved(kv.Key) < kv.Value &&
                                         w.Owned(team).Any(s => s.IsStructure && s.IsComplete && s.Def.Recipes.Any(r => r.Inputs.ContainsKey(kv.Key)))).ToList();
            if (eaten.Count == 0) return "";
            var k = eaten[0];
            return $". Your converters use {k.Key} up as it comes in; to bank some, set a reserve: {{\"type\":\"reserve\",\"item\":\"{k.Key}\",\"amount\":{k.Value}}}";
        }

        static JObj Load(World w, int team, Dictionary<string, object> c)
        {
            var t = w.Get((int)c.Num("transport", c.Num("target", 0)));
            if (t == null || t.Team != team || t.Def.Capacity == 0) return Err("transport must be one of your apc or transport_chopper ids");
            var inf = ResolveUnits(w, team, c).Where(u => u.Def.Armor == Armor.Infantry).ToList();
            if (inf.Count == 0) return Err("only infantry can board; none given");
            int seats = w.FreeSeats(t);
            if (seats <= 0) return Err($"{t.Def.Key} #{t.Id} is full ({t.Passengers.Count}/{t.Def.Capacity})");
            var going = inf.Take(seats).ToList();
            foreach (var u in going) w.SetOrder(u, Order.Board, t.Pos, t.Id);
            return Ok($"{going.Count} infantry boarding {t.Def.Key} #{t.Id}" + (inf.Count > going.Count ? $"; {inf.Count - going.Count} left behind (no room)" : ""));
        }

        static JObj Unload(World w, int team, Dictionary<string, object> c)
        {
            var ts = ResolveUnits(w, team, c).Where(u => u.Def.Capacity > 0).ToList();
            if (ts.Count == 0) return Err("no transports given");
            int n = ts.Sum(w.Unload);
            return n > 0 ? Ok($"{n} passenger(s) unloaded") : Err("those transports are empty");
        }

        static JObj Capture(World w, int team, Dictionary<string, object> c)
        {
            var eng = ResolveUnits(w, team, c).Where(u => u.Def.Engineer).ToList();
            if (eng.Count == 0) return Err("no engineers given");
            var t = w.Get((int)c.Num("target", 0));
            // Derricks get a new id each time one is rebuilt: x,y names the site instead.
            float cx = c.Num("x"), cy = c.Num("y");
            if (t == null && !float.IsNaN(cx) && !float.IsNaN(cy))
            {
                var at = new Vec2(cx, cy);
                t = w.Derricks.Where(d => Vec2.Dist(d.Center, at) <= 3f).OrderBy(d => Vec2.Dist(d.Center, at)).FirstOrDefault();
                // A destroyed derrick's site: go and wait there, and take the new one when it rises.
                var pending = w.DerrickRespawns.Where(r => Vec2.Dist(new Vec2(r.origin.X + 1, r.origin.Y + 1), at) <= 3f).Select(r => (Int2?)r.origin).FirstOrDefault();
                if (t == null && pending is Int2 o)
                {
                    var site = new Vec2(o.X + 1, o.Y + 1);
                    float due = w.DerrickRespawns.First(r => r.origin.Equals(o)).at - w.Time;
                    foreach (var u in eng) w.SetOrder(u, Order.Capture, site, 0);
                    return Ok($"{eng.Count} engineer(s) heading for the derrick site at {o.X},{o.Y}: it's rebuilt neutral in about {MathF.Max(0, due):0}s, and they take it then (it pays {World.DerrickSteel} steel/s while you hold it)");
                }
            }
            if (t == null) return Err("target must be an enemy structure or a derrick: give its id, or x,y of a derrick (ids change when one is destroyed and rebuilt; see 'derricks' in state)");
            if (!t.IsStructure || t.Team == team) return Err("target must be an enemy structure, or a neutral derrick");
            // Derricks are landmarks everyone knows about: an engineer can set out for one unseen. Whether it's still neutral,
            // or now someone's and weak enough to take, is found out on arrival, so the order leaks nothing.
            if (t.Def.Key == "derrick" && !w.IsVisibleTo(team, t))
            {
                foreach (var u in eng) w.SetOrder(u, Order.Capture, t.Center, t.Id);
                return Ok($"{eng.Count} engineer(s) heading for derrick #{t.Id}, out of sight: if it's still neutral when they get there they take it " +
                          $"(it pays {World.DerrickSteel} steel/s while you hold it); if someone holds it, it must be below 50% hp");
            }
            if (!w.IsVisibleTo(team, t)) return Err("target is not currently visible");
            if (w.IsProtected(t.Team)) return Err("that player is under newcomer protection");
            if (t.Team >= 0 && w.IsProtected(team))
                return Err($"you're under newcomer protection for {(int)(w.Teams[team].ProtectedUntil - w.Time)}s more and can't take other players' buildings yet; neutral derricks are fine");
            if (!t.IsComplete) return Err("can't capture a structure that's still under construction");
            if (!World.Capturable(t))
                return Err($"{t.Def.Key} #{t.Id} is at {(int)t.Hp}/{t.Def.MaxHp}; damage it below 50% before an engineer can capture it");
            foreach (var u in eng) w.SetOrder(u, Order.Capture, t.Center, t.Id);
            return Ok($"{eng.Count} engineer(s) moving to capture {(t.Team < 0 ? "the neutral " : "")}{t.Def.Key} #{t.Id}" + (t.Def.Key == "derrick" ? $" (it pays {World.DerrickSteel} steel/s while you hold it)" : ""));
        }

        /// <summary>A capture order whose target is a derrick: neutral, or unseen (decided on arrival).</summary>
        static bool CapturesNeutral(World w, Dictionary<string, object> c)
        {
            var t = w.Get((int)c.Num("target", 0));
            if (t != null) return t.Def.Key == "derrick";
            float x = c.Num("x"), y = c.Num("y"); // a derrick's site by x,y (standing or being rebuilt)
            return !float.IsNaN(x) && !float.IsNaN(y);
        }

        static readonly Vec2[] MinePattern =
        {
            new Vec2(0, 0), new Vec2(1.2f, 0), new Vec2(-1.2f, 0), new Vec2(0, 1.2f), new Vec2(0, -1.2f), new Vec2(1.2f, 1.2f), new Vec2(-1.2f, -1.2f), new Vec2(1.2f, -1.2f),
        };

        static JObj LayMines(World w, int team, Dictionary<string, object> c)
        {
            var ls = ResolveUnits(w, team, c).Where(u => u.Def.LaysMines).ToList();
            if (ls.Count == 0) return Err("no mine layers given");
            float x = c.Num("x"), y = c.Num("y");
            if (float.IsNaN(x) || float.IsNaN(y)) return Err("x and y are required");
            int count = Math.Max(1, Math.Min(8, (int)c.Num("count", 1)));
            if (w.Teams[team].Amount("steel") < EntityDef.MineCost) return Err($"mines cost {EntityDef.MineCost} steel each; you have {w.Teams[team].Amount("steel")}");
            var spots = MinePattern.Take(count).Select(o => new Vec2(x, y) + o).ToList();
            for (int i = 0; i < ls.Count; i++)
            {
                var l = ls[i];
                w.SetOrder(l, Order.LayMines, new Vec2(x, y));
                l.MineQueue.Clear();
                // Split the pattern between layers.
                for (int k = i; k < spots.Count; k += ls.Count) l.MineQueue.Add(spots[k]);
            }
            return Ok($"laying {count} mine(s) around ({x:0},{y:0}); {EntityDef.MineCost} steel each, paid as each is laid");
        }

        static JObj Repair(World w, int team, Dictionary<string, object> c)
        {
            var healers = ResolveUnits(w, team, c).Where(u => u.Def.RepairRate > 0).ToList();
            if (healers.Count == 0) return Err("no repair trucks or medics given");
            var target = w.Get((int)c.Num("target", 0));
            if (target == null || target.Team != team) return Err("target must be one of your own units or structures");
            if (!target.IsComplete) return Err("that structure is still under construction");
            bool needsFuel = target.Def.UsesFuel && !target.IsAir && target.Fuel < target.FuelMax * 0.99f;
            if (target.Hp >= target.Def.MaxHp - 0.5f && !needsFuel) return Err($"{target.Def.Key} #{target.Id} is already at full health" + (target.Def.UsesFuel ? " and fuel" : ""));
            // Medics take infantry, repair trucks take machines; whoever can't help is left alone.
            var able = healers.Where(h => World.CanTend(h, target) || (needsFuel && !h.Def.Medic)).ToList();
            if (able.Count == 0)
                return Err(target.Def.Armor == Armor.Infantry ? "only medics can heal infantry" : "only repair trucks can repair vehicles, aircraft and structures");
            foreach (var u in able) w.SetOrder(u, Order.Repair, target.Center, target.Id);
            return Ok($"{able.Count} {(target.Def.Armor == Armor.Infantry ? "medic(s) healing" : "repair truck(s) repairing")} {target.Def.Key} #{target.Id} ({(int)target.Hp}/{target.Def.MaxHp})" +
                      (able.Count < healers.Count ? $"; {healers.Count - able.Count} can't work on that" : ""));
        }

        static JObj Survey(World w, int team, Dictionary<string, object> c)
        {
            var units = ResolveUnits(w, team, c).Where(u => u.Def.Key == "geological_surveyor").ToList();
            if (units.Count == 0) return Err("no surveyors given (train a geological_surveyor at a factory)");
            float x = c.Num("x"), y = c.Num("y");
            if (float.IsNaN(x) || float.IsNaN(y)) return Err("x and y are required: where to survey");
            if (!w.Map.InBounds((int)x, (int)y)) return Err($"({x},{y}) is outside the {w.Map.W}x{w.Map.H} map");
            foreach (var u in units) { w.SetOrder(u, Order.Survey, new Vec2(x, y)); u.WorkTimer = 0; u.Prospecting = false; u.SurveyFailure = null; }
            return Ok($"{units.Count} surveyor(s) heading to {x},{y}; a survey takes {EntityDef.SurveySeconds:0}s and covers {EntityDef.SurveyRadius:0} tiles, and every deposit found gets a flag (a mining zone in your state)");
        }

        public const float DefaultProspectRadius = 40f;

        static JObj Prospect(World w, int team, Dictionary<string, object> c)
        {
            var units = ResolveUnits(w, team, c).Where(u => u.Def.Key == "geological_surveyor").ToList();
            if (units.Count == 0) return Err("no surveyors given (train a geological_surveyor at a factory)");
            float x = c.Num("x"), y = c.Num("y");
            if (float.IsNaN(x) != float.IsNaN(y)) return Err("give both x and y (the centre of the area to prospect), or neither (around each surveyor)");
            if (!float.IsNaN(x) && !w.Map.InBounds((int)x, (int)y)) return Err($"({x},{y}) is outside the {w.Map.W}x{w.Map.H} map");
            float radius = c.Num("radius", DefaultProspectRadius);
            if (float.IsNaN(radius) || radius < 6) return Err("radius must be at least 6 tiles");
            radius = MathF.Min(radius, MathF.Max(w.Map.W, w.Map.H) * 1.5f);
            var sent = new List<string>(); var none = new List<string>();
            foreach (var u in units)
            {
                var centre = float.IsNaN(x) ? u.Pos : new Vec2(x, y);
                u.SkipSites.Clear();
                var site = w.FindProspectSite(u, centre, radius);
                if (!site.HasValue) { none.Add($"#{u.Id}: nothing unsurveyed it can reach within {radius:0} tiles of {(int)centre.X},{(int)centre.Y}"); continue; }
                w.SetOrder(u, Order.Survey, site.Value);
                u.WorkTimer = 0; u.Prospecting = true; u.ProspectCenter = centre; u.ProspectRadius = radius; u.SurveyFailure = null;
                sent.Add($"#{u.Id} heads first to {(int)site.Value.X},{(int)site.Value.Y}, {Vec2.Dist(u.Pos, site.Value):0} tiles away");
            }
            if (sent.Count == 0) return Err(string.Join("; ", none) + ". Pick another area (x,y) or a bigger radius.");
            return Ok($"{sent.Count} surveyor(s) prospecting within {radius:0} tiles ({string.Join(", ", sent)}): each survey takes {EntityDef.SurveySeconds:0}s, " +
                      $"flags every deep deposit within {EntityDef.SurveyRadius:0} tiles as a mining zone, then they move on to the nearest unsurveyed spot until nothing is left or you give another order" +
                      (none.Count > 0 ? "; " + string.Join("; ", none) : ""));
        }

        static JObj Drill(World w, int team, Dictionary<string, object> c)
        {
            var rigs = ResolveUnits(w, team, c).Where(u => u.Def.DeploysInto == "deep_mine").ToList();
            if (rigs.Count == 0) return Err("no drill rigs given (train a drill_rig at a factory)");
            var t = w.Teams[team];
            string Desc(DeepDeposit d) => $"zone #{d.Id} ({Defs.Ores[d.Type]} at {(int)d.Pos.X},{(int)d.Pos.Y}, {(int)d.Amount} left)";
            // Free zones nobody of ours is already driving to.
            var open = w.Map.Deep.Where(d => t.Surveyed.Contains(d.Id) && w.DrillBlocker(d, team) == null &&
                                             !w.RigsBound(team, d.Id).Any(r => !rigs.Contains(r))).ToList();
            string FreeList() => open.Count == 0 ? "you have no free mining zones: survey or prospect with a geological_surveyor to flag some"
                                                 : "free zones: " + string.Join(", ", open.OrderBy(d => Vec2.Dist(d.Pos, rigs[0].Pos)).Take(6).Select(d => $"#{d.Id} {Defs.Ores[d.Type]}"));
            void Send(Entity rig, DeepDeposit d) { w.SetOrder(rig, Order.Drill, d.Pos); rig.ZoneId = d.Id; }
            // Like harvest: say so when a zone sits by an enemy base you know of (a rig is 800 steel).
            string Danger(DeepDeposit d)
            {
                var foe = t.KnownEnemyStructures.Values.Where(k => k.team >= 0 && k.key != "derrick" && Vec2.Dist(k.origin.Center, d.Pos) < World.HostileOreRadius)
                           .Select(k => $"{w.Teams[k.team].Name}'s {k.key}").FirstOrDefault();
                return foe == null ? "" : $" (careful: by {foe}, which you know of: escort it)";
            }

            if (c.ContainsKey("zone"))
            {
                int id = (int)c.Num("zone", 0);
                var d = w.Map.DepositById(id);
                if (d == null || !t.Surveyed.Contains(id)) return Err($"zone #{id} isn't one of your mining zones; {FreeList()}");
                var why = w.DrillBlocker(d, team);
                if (why != null) return Err($"{why}; {FreeList()}");
                var bound = w.RigsBound(team, id).Where(r => !rigs.Contains(r)).ToList();
                if (bound.Count > 0) return Err($"drill_rig #{bound[0].Id} is already on its way to zone #{id} (one mine per zone); {FreeList()}");
                var rig = rigs.OrderBy(r => Vec2.Dist(r.Pos, d.Pos)).First();
                Send(rig, d);
                foreach (var other in rigs.Where(r => r != rig && r.ZoneId == id && r.Order == Order.Drill)) w.SetOrder(other, Order.Idle, other.Pos);
                return Ok($"drill_rig #{rig.Id} heading to {Desc(d)}, {Vec2.Dist(rig.Pos, d.Pos):0} tiles away{Danger(d)}; it deploys into a deep_mine on arrival" +
                          (rigs.Count > 1 ? $". One mine per zone: {string.Join(", ", rigs.Where(r => r != rig).Select(r => "#" + r.Id))} not sent" : ""));
            }

            // No zone given: each rig takes the nearest free zone nobody else is heading for.
            var results = new List<string>();
            foreach (var rig in rigs)
            {
                var d = open.OrderBy(z => Vec2.Dist(z.Pos, rig.Pos)).FirstOrDefault();
                if (d == null) { results.Add($"#{rig.Id}: no free zone left for it"); continue; }
                open.Remove(d);
                Send(rig, d);
                results.Add($"#{rig.Id} heading to {Desc(d)}{Danger(d)}");
            }
            if (!results.Any(r => r.Contains("heading"))) return Err(FreeList());
            return Ok(string.Join("; ", results) + "; each deploys into a deep_mine on arrival");
        }

        static JObj SetRetreat(World w, int team, Dictionary<string, object> c)
        {
            var units = ResolveUnits(w, team, c).Where(u => !u.IsMine).ToList();
            if (units.Count == 0) return Err("no valid units of yours given");
            float pct = c.Num("below_pct");
            if (float.IsNaN(pct) || pct < 0 || pct > 95) return Err("below_pct must be 0-95 (0 turns it off)");
            foreach (var u in units) { u.RetreatBelow = pct / 100f; if (pct == 0) u.Retreating = false; }
            return Ok(pct == 0 ? $"{units.Count} unit(s) will fight to the end" : $"{units.Count} unit(s) will pull back to base on their own below {pct:0}% HP");
        }

        /// <summary>[[x,y],...] or [{"x":..,"y":..},...]; empty list if absent, null if malformed.</summary>
        static List<Vec2> ParsePoints(object raw)
        {
            var list = new List<Vec2>();
            if (raw == null) return list;
            if (raw is not List<object> items || items.Count > 20) return null;
            foreach (var it in items)
            {
                float px, py;
                if (it is List<object> pair && pair.Count == 2) { px = Convert.ToSingle(pair[0]); py = Convert.ToSingle(pair[1]); }
                else if (it is Dictionary<string, object> d && d.ContainsKey("x") && d.ContainsKey("y")) { px = Convert.ToSingle(d["x"]); py = Convert.ToSingle(d["y"]); }
                else return null;
                list.Add(new Vec2(px, py));
            }
            return list;
        }

        static JObj Refuel(World w, int team, Dictionary<string, object> c)
        {
            var units = ResolveUnits(w, team, c).Where(u => u.Def.UsesFuel && !u.IsCarried).ToList();
            if (units.Count == 0) return Err("no vehicles or aircraft given (infantry don't use fuel)");
            var at = c.ContainsKey("target") ? w.Get((int)c.Num("target", 0)) : null;
            var sent = new List<string>(); var stuck = new List<string>();
            foreach (var u in units)
            {
                var p = at != null && World.IsFuelPoint(u, at) ? at : w.NearestFuelPoint(u);
                if (u.Stranded) { stuck.Add($"#{u.Id} is out of fuel; send a repair truck to it with repair"); continue; }
                if (p == null) { stuck.Add($"#{u.Id} has nowhere to refuel ({(u.IsAir ? "build an airfield" : "needs a command center, outpost, refinery, factory or a repair truck")})"); continue; }
                w.BeginRefuel(u, p);
                sent.Add($"#{u.Id} ({StateView.Pct(u.FuelFraction)}%) to {p.Def.Key} #{p.Id}");
            }
            if (sent.Count == 0) return Err(string.Join("; ", stuck));
            return Ok("refuelling: " + string.Join(", ", sent) + (stuck.Count > 0 ? "; " + string.Join("; ", stuck) : ""));
        }

        static JObj Deploy(World w, int team, Dictionary<string, object> c)
        {
            var units = ResolveUnits(w, team, c).Where(u => u.Def.DeploysInto != null).ToList();
            if (units.Count == 0) return Err("no deployable units given (outpost_truck, construction_truck, drill_rig)");
            var results = new List<string>();
            foreach (var u in units)
            {
                var why = w.Deploy(u);
                // Say where: a truck still rolling when the order lands deploys where it is now, not where it was last seen.
                var built = why == null ? w.Owned(team).Where(s => s.IsStructure && s.Def.Key == u.Def.DeploysInto).OrderByDescending(s => s.Id).First() : null;
                results.Add(why == null ? $"#{u.Id} deployed into {u.Def.DeploysInto} #{built.Id} at ({built.Origin.X},{built.Origin.Y})" : $"#{u.Id} {why}");
            }
            bool ok = results.Any(r => r.Contains("deployed"));
            return ok ? Ok(string.Join("; ", results)) : Err(string.Join("; ", results));
        }

        static JObj Cancel(World w, int team, Dictionary<string, object> c)
        {
            var key = c.Str("unit");
            var def = w.Def(key);
            if (def == null || def.IsStructure) return Err("unit key required");
            var q = w.Teams[team].UnitQueues[def.BuiltBy];
            int idx = q.FindLastIndex(p => p.Key == key);
            if (idx < 0) return Err($"no {key} in queue");
            q.RemoveAt(idx);
            w.Teams[team].Pay(def.Cost, -1f);
            return Ok($"cancelled one {key}, refunded {def.CostText}");
        }
    }
}
