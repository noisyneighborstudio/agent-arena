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
  {""type"":""move"", ""units"":[IDS], ""x"":X, ""y"":Y}     move, ignoring enemies
  {""type"":""attack_move"", ""units"":[IDS], ""x"":X, ""y"":Y}  move, engaging enemies on the way
  {""type"":""attack"", ""units"":[IDS], ""target"":ID}     focus fire on a visible enemy
  {""type"":""stop"", ""units"":[IDS]}
  {""type"":""harvest"", ""units"":[IDS], ""x"":X, ""y"":Y}  send harvesters to ore near x,y
  {""type"":""rally"", ""structure_id"":ID, ""x"":X, ""y"":Y} where new units from that building go
  {""type"":""sell"", ""structure_id"":ID}                 sell for 50% refund
  {""type"":""cancel"", ""unit"":KEY}                       cancel the last queued unit of that type (full refund)
  {""type"":""say"", ""text"":""...""}                      broadcast a chat message (shown on screen)
'units' may also be the string ""all"" (all your combat units) or ""idle"" (idle combat units).";

        public static JObj Execute(World w, int team, Dictionary<string, object> c)
        {
            try
            {
                if (team < 0 || team >= w.Teams.Count) return Err("bad team");
                if (w.GameOver) return Err("game is over");
                if (w.Teams[team].Defeated) return Err("your team is defeated");
                var type = c.Str("type", "").ToLowerInvariant();
                switch (type)
                {
                    case "build": return Build(w, team, c);
                    case "train": return Train(w, team, c);
                    case "move": return UnitOrder(w, team, c, Order.Move);
                    case "attack_move": return UnitOrder(w, team, c, Order.AttackMove);
                    case "attack": return Attack(w, team, c);
                    case "stop": return UnitOrder(w, team, c, Order.Idle);
                    case "harvest": return UnitOrder(w, team, c, Order.Harvest);
                    case "rally": return Rally(w, team, c);
                    case "sell": return Sell(w, team, c);
                    case "cancel": return Cancel(w, team, c);
                    case "say":
                        {
                            var text = c.Str("text", "");
                            if (text.Length > 280) text = text.Substring(0, 280);
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
            if (def == null || !def.IsStructure) return Err($"unknown structure '{key}'. Valid: {string.Join(", ", Defs.All.Values.Where(d => d.IsStructure && d.BuiltBy != Producer.None).Select(d => d.Key))}");
            var missing = w.MissingPrereq(team, def);
            if (missing != null) return Err($"{key} {missing}");
            var t = w.Teams[team];
            if (t.Credits < def.Cost) return Err($"not enough credits ({t.Credits}/{def.Cost})");
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
            t.Credits -= def.Cost;
            t.Stats.CreditsSpent += def.Cost;
            var s = w.SpawnStructure(team, key, origin, 0f);
            t.StructureQueue.Add(new ProdItem { Key = key, StructureId = s.Id });
            w.Emit("placed", team, s.Id, pos: s.Center, key: key);
            int ahead = t.StructureQueue.Count - 1;
            return Ok($"{key} #{s.Id} placed at ({origin.X},{origin.Y}), size {def.SizeX}x{def.SizeY}, build time {def.BuildTime}s" + (ahead > 0 ? $", {ahead} structure(s) ahead in queue" : ""))
                .Set("id", s.Id);
        }

        static JObj Train(World w, int team, Dictionary<string, object> c)
        {
            var key = c.Str("unit") ?? c.Str("key");
            var def = Defs.Get(key);
            if (def == null || def.IsStructure) return Err($"unknown unit '{key}'. Valid: {string.Join(", ", Defs.All.Values.Where(d => !d.IsStructure).Select(d => d.Key))}");
            var missing = w.MissingPrereq(team, def);
            if (missing != null) return Err($"{key} {missing}");
            int count = (int)c.Num("count", 1);
            count = Math.Max(1, Math.Min(10, count));
            var t = w.Teams[team];
            int queued = 0;
            for (int i = 0; i < count; i++)
            {
                if (t.Credits < def.Cost) break;
                t.Credits -= def.Cost;
                t.Stats.CreditsSpent += def.Cost;
                t.UnitQueues[def.BuiltBy].Add(new ProdItem { Key = key });
                queued++;
            }
            if (queued == 0) return Err($"not enough credits ({t.Credits}/{def.Cost})");
            return Ok($"queued {queued}x {key}" + (queued < count ? $" (could only afford {queued})" : "") + $"; queue length {t.UnitQueues[def.BuiltBy].Count}");
        }

        static List<Entity> ResolveUnits(World w, int team, Dictionary<string, object> c)
        {
            if (c.TryGetValue("units", out var v) && v is string s)
            {
                var mine = w.Owned(team).Where(e => !e.IsStructure && e.IsArmed);
                if (s == "idle") mine = mine.Where(e => e.Order == Order.Idle);
                return mine.ToList();
            }
            return c.Ids("units").Select(w.Get).Where(e => e != null && e.Team == team && !e.IsStructure).ToList();
        }

        static JObj UnitOrder(World w, int team, Dictionary<string, object> c, Order order)
        {
            var units = ResolveUnits(w, team, c);
            if (units.Count == 0) return Err("no valid units of yours given (use ids from your unit list)");
            float x = c.Num("x"), y = c.Num("y");
            if (order != Order.Idle && (float.IsNaN(x) || float.IsNaN(y)))
            {
                if (order != Order.Harvest) return Err("x and y are required");
            }
            var dest = new Vec2(float.IsNaN(x) ? 0 : x, float.IsNaN(y) ? 0 : y);
            if (order != Order.Idle && !float.IsNaN(x) && !w.Map.InBounds((int)x, (int)y)) return Err($"({x},{y}) is outside the {w.Map.W}x{w.Map.H} map");

            if (order == Order.Harvest)
            {
                var hs = units.Where(u => u.IsHarvester).ToList();
                if (hs.Count == 0) return Err("none of those units are harvesters");
                foreach (var h in hs)
                {
                    var tile = float.IsNaN(x) ? w.Map.NearestOre(h.Pos, 60) : w.Map.NearestOre(dest, 12);
                    w.SetOrder(h, Order.Harvest, tile?.Center ?? h.Pos);
                }
                return Ok($"{hs.Count} harvester(s) harvesting");
            }

            // Spread a group around the destination so they don't all fight for one tile.
            int n = units.Count, i = 0;
            int cols = (int)MathF.Ceiling(MathF.Sqrt(n));
            foreach (var u in units)
            {
                var off = new Vec2((i % cols - (cols - 1) / 2f) * 0.9f, (i / cols - (cols - 1) / 2f) * 0.9f);
                i++;
                var target = n > 1 && order != Order.Idle ? dest + off : dest;
                if (u.IsHarvester && order == Order.AttackMove) { w.SetOrder(u, Order.Move, target); continue; }
                w.SetOrder(u, order, order == Order.Idle ? u.Pos : target);
            }
            return Ok($"{n} unit(s) {(order == Order.Idle ? "stopped" : order.ToString().ToLowerInvariant())}");
        }

        static JObj Attack(World w, int team, Dictionary<string, object> c)
        {
            var units = ResolveUnits(w, team, c).Where(u => u.IsArmed).ToList();
            if (units.Count == 0) return Err("no valid armed units of yours given");
            var target = w.Get((int)c.Num("target", 0));
            if (target == null) return Err("target not found (it may be destroyed)");
            if (target.Team == team) return Err("that is your own unit");
            if (!w.IsVisibleTo(team, target)) return Err("target is not currently visible; use attack_move toward its last known position");
            foreach (var u in units) w.SetOrder(u, Order.Attack, target.Center, target.Id);
            return Ok($"{units.Count} unit(s) attacking {target.Def.Key} #{target.Id}");
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
            return Ok($"rally point for {s.Def.Key} set to ({x},{y})");
        }

        static JObj Sell(World w, int team, Dictionary<string, object> c)
        {
            var s = OwnStructure(w, team, c);
            if (s == null) return Err("structure_id must be one of your structures");
            int refund = (int)(s.Def.Cost * 0.5f * s.BuildProgress * (s.Hp / s.Def.MaxHp));
            if (!s.IsComplete) refund = (int)(s.Def.Cost * (1 - s.BuildProgress) + s.Def.Cost * 0.5f * s.BuildProgress);
            w.Teams[team].Credits += refund;
            w.Emit("sold", team, s.Id, pos: s.Center, key: s.Def.Key);
            w.Emit("destroyed", team, s.Id, 0, s.Center, key: s.Def.Key);
            w.Remove(s);
            return Ok($"sold {s.Def.Key} for {refund}");
        }

        static JObj Cancel(World w, int team, Dictionary<string, object> c)
        {
            var key = c.Str("unit");
            var def = Defs.Get(key);
            if (def == null || def.IsStructure) return Err("unit key required");
            var q = w.Teams[team].UnitQueues[def.BuiltBy];
            int idx = q.FindLastIndex(p => p.Key == key);
            if (idx < 0) return Err($"no {key} in queue");
            q.RemoveAt(idx);
            w.Teams[team].Credits += def.Cost;
            return Ok($"cancelled one {key}, refunded {def.Cost}");
        }
    }
}
