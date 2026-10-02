using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Pez.Sim
{
    /// <summary>What a team is allowed to know, shaped for LLM consumption. Enemies are fogged.</summary>
    public static class StateView
    {
        static string R(float v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        public static JObj TeamState(World w, int team, long sinceSeq = 0)
        {
            var t = w.Teams[team];
            var o = new JObj()
                .Set("tick", w.Tick)
                .Set("time_s", (float)Math.Round(w.Time, 1))
                .Set("game_over", w.GameOver)
                .Set("winner", w.Winner >= 0 ? w.Teams[w.Winner].Name : null)
                .Set("you", new JObj()
                    .Set("team", team).Set("name", t.Name).Set("player", t.PlayerName)
                    .Set("credits", t.Credits)
                    .Set("power", $"{t.PowerProduced} produced / {t.PowerUsed} used" + (t.LowPower ? " (LOW POWER: production at half speed, build a power_plant)" : ""))
                    .Set("start", $"{R(t.StartPos.X)},{R(t.StartPos.Y)}"))
                .Set("map", $"{w.Map.W}x{w.Map.H} tiles; x grows east, y grows north");

            var enemies = w.Teams.Where(x => x.Id != team).Select(x => new JObj()
                .Set("team", x.Id).Set("name", x.Name).Set("player", x.PlayerName ?? x.Controller)
                .Set("start", $"{R(x.StartPos.X)},{R(x.StartPos.Y)}").Set("defeated", x.Defeated)).ToList();
            o.Set("opponents", enemies);

            var prod = new JObj();
            prod.Set("structures", t.StructureQueue.Select(p => { var s = w.Get(p.StructureId); return $"{p.Key} #{p.StructureId} {(int)((s?.BuildProgress ?? 0) * 100)}%"; }).ToList());
            foreach (var kv in t.UnitQueues)
            {
                var name = kv.Key == Producer.Barracks ? "barracks" : "war_factory";
                prod.Set(name, kv.Value.Select((p, i) => i == 0 ? $"{p.Key} {(int)(p.Progress / Defs.Get(p.Key).BuildTime * 100)}%" : p.Key).ToList());
            }
            o.Set("production", prod);

            var available = new List<string>();
            foreach (var d in Defs.All.Values)
                if (d.BuiltBy != Producer.None && w.MissingPrereq(team, d) == null) available.Add($"{d.Key} ${d.Cost}");
            o.Set("available", available);

            o.Set("my_structures", w.Owned(team).Where(e => e.IsStructure).Select(e =>
            {
                var s = $"#{e.Id} {e.Def.Key} at {e.Origin.X},{e.Origin.Y} ({e.Def.SizeX}x{e.Def.SizeY}) hp {(int)e.Hp}/{e.Def.MaxHp}";
                if (!e.IsComplete) s += $" BUILDING {(int)(e.BuildProgress * 100)}%";
                if (w.Time - e.LastHitTime < 5) s += " UNDER ATTACK";
                return s;
            }).ToList());

            o.Set("my_units", w.Owned(team).Where(e => !e.IsStructure).Select(e =>
            {
                var s = $"#{e.Id} {e.Def.Key} at {R(e.Pos.X)},{R(e.Pos.Y)} hp {(int)e.Hp}/{e.Def.MaxHp} {e.OrderName}";
                if (e.Order == Order.Attack) s += $" #{e.TargetId}";
                if (e.IsHarvester) s += $" cargo {e.Cargo}/{e.Def.HarvestCapacity}";
                return s;
            }).ToList());

            o.Set("visible_enemies", w.Entities.Where(e => !e.Dead && e.Team != team && w.IsVisibleTo(team, e)).Select(e =>
                e.IsStructure
                    ? $"#{e.Id} team{e.Team} {e.Def.Key} at {e.Origin.X},{e.Origin.Y} ({e.Def.SizeX}x{e.Def.SizeY}) hp {(int)e.Hp}/{e.Def.MaxHp}"
                    : $"#{e.Id} team{e.Team} {e.Def.Key} at {R(e.Pos.X)},{R(e.Pos.Y)} hp {(int)e.Hp}/{e.Def.MaxHp}").ToList());

            o.Set("remembered_enemy_structures", t.KnownEnemyStructures
                .Where(kv => { var e = w.Get(kv.Key); return e == null || !w.IsVisibleTo(team, e); })
                .Select(kv => $"#{kv.Key} team{kv.Value.team} {kv.Value.key} at {kv.Value.origin.X},{kv.Value.origin.Y} (last seen)").ToList());

            o.Set("ore_fields", OreFields(w).Select(f => $"around {f.cx},{f.cy}: {f.tiles} tiles, {f.total} credits").ToList());

            o.Set("stats", new JObj()
                .Set("kills", t.Stats.Kills).Set("units_lost", t.Stats.UnitsLost).Set("structures_lost", t.Stats.StructuresLost)
                .Set("ore_harvested", t.Stats.OreHarvested));

            o.Set("events", EventsFor(w, team, sinceSeq, 25));
            o.Set("last_event_seq", w.Events.Count > 0 ? w.Events[w.Events.Count - 1].Seq : 0);
            return o;
        }

        public static List<string> EventsFor(World w, int team, long sinceSeq, int max)
        {
            var list = new List<string>();
            for (int i = w.Events.Count - 1; i >= 0 && list.Count < max; i--)
            {
                var e = w.Events[i];
                if (e.Seq <= sinceSeq) break;
                string s = null;
                string ts = $"[{e.Tick * World.Dt:0}s]";
                switch (e.Type)
                {
                    case "chat": s = $"{ts} {(e.Team == team ? "you" : TeamLabel(w, e.Team))} said: {e.Text}"; break;
                    case "built": if (e.Team == team) s = $"{ts} {e.Key} #{e.A} completed"; break;
                    case "trained": if (e.Team == team) s = $"{ts} {e.Key} #{e.A} ready"; break;
                    case "under_attack": if (e.Team == team) s = $"{ts} your {e.Key} #{e.A} is under attack"; break;
                    case "destroyed":
                        if (e.Team == team) s = $"{ts} LOST your {e.Key} #{e.A}";
                        else if (w.Teams[team].Visible[w.Map.Idx((int)e.Pos.X, (int)e.Pos.Y)]) s = $"{ts} destroyed enemy {e.Key} #{e.A}";
                        break;
                    case "defeated": case "game_over": s = $"{ts} {e.Text}"; break;
                }
                if (s != null) list.Add(s);
            }
            list.Reverse();
            return list;
        }

        static string TeamLabel(World w, int team) => team >= 0 ? $"{w.Teams[team].Name} ({w.Teams[team].PlayerName ?? w.Teams[team].Controller})" : "server";

        public static List<(int cx, int cy, int tiles, int total)> OreFields(World w)
        {
            var m = w.Map;
            var seen = new bool[m.W * m.H];
            var result = new List<(int, int, int, int)>();
            for (int i = 0; i < m.Ore.Length; i++)
            {
                if (m.Ore[i] <= 0 || seen[i]) continue;
                var q = new Queue<int>(); q.Enqueue(i); seen[i] = true;
                long sx = 0, sy = 0; int n = 0, total = 0;
                while (q.Count > 0)
                {
                    int c = q.Dequeue(); int x = c % m.W, y = c / m.W;
                    sx += x; sy += y; n++; total += m.Ore[c];
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (!m.InBounds(nx, ny)) continue;
                            int ni = m.Idx(nx, ny);
                            if (seen[ni] || m.Ore[ni] <= 0) continue;
                            seen[ni] = true; q.Enqueue(ni);
                        }
                }
                result.Add(((int)(sx / n), (int)(sy / n), n, total));
            }
            return result;
        }

        static readonly Dictionary<string, char> Glyph = new Dictionary<string, char>
        {
            { "construction_yard", 'C' }, { "power_plant", 'P' }, { "refinery", 'R' }, { "barracks", 'B' }, { "war_factory", 'F' }, { "gun_turret", 'T' },
        };

        /// <summary>ASCII map from the team's perspective. North (high y) is at the top.</summary>
        public static string AsciiMap(World w, int team)
        {
            var m = w.Map;
            var g = new char[m.W * m.H];
            for (int i = 0; i < g.Length; i++)
            {
                g[i] = m.Tiles[i] switch { Terrain.Rock => '#', Terrain.Water => '~', _ => '.' };
                if (m.Ore[i] > 0) g[i] = '$';
            }
            var vis = w.Teams[team].Visible;
            foreach (var kv in w.Teams[team].KnownEnemyStructures)
            {
                var d = Defs.Get(kv.Value.key);
                for (int y = 0; y < d.SizeY; y++) for (int x = 0; x < d.SizeX; x++)
                        g[m.Idx(kv.Value.origin.X + x, kv.Value.origin.Y + y)] = char.ToLowerInvariant(Glyph[d.Key]);
            }
            foreach (var e in w.Entities)
            {
                if (e.Dead) continue;
                bool mine = e.Team == team;
                if (!mine && !w.IsVisibleTo(team, e)) continue;
                if (e.IsStructure)
                {
                    char ch = Glyph[e.Def.Key];
                    if (!mine) ch = char.ToLowerInvariant(ch);
                    for (int y = 0; y < e.Def.SizeY; y++) for (int x = 0; x < e.Def.SizeX; x++) g[m.Idx(e.Origin.X + x, e.Origin.Y + y)] = ch;
                }
            }
            foreach (var e in w.Entities)
            {
                if (e.Dead || e.IsStructure) continue;
                bool mine = e.Team == team;
                if (!mine && !w.IsVisibleTo(team, e)) continue;
                var t = Int2.Of(e.Pos);
                if (!m.InBounds(t.X, t.Y)) continue;
                g[m.Idx(t.X, t.Y)] = mine ? (e.IsHarvester ? 'h' : e.Def.Armor == Armor.Vehicle ? 'v' : 'i') : (e.IsHarvester ? 'H' : e.Def.Armor == Armor.Vehicle ? 'X' : 'x');
            }
            var sb = new StringBuilder();
            sb.AppendLine("Legend: . open  $ ore  # rock  ~ water | YOUR structures: C conyard P power R refinery B barracks F factory T turret | enemy structures: same letters lowercase | your units: i infantry v vehicle h harvester | enemy units: x infantry X vehicle H harvester | fog: enemies outside your vision are hidden (structures you've seen stay drawn)");
            sb.Append("    ");
            for (int x = 0; x < m.W; x++) sb.Append(x % 10 == 0 ? (char)('0' + (x / 10) % 10) : ' ');
            sb.AppendLine();
            sb.Append("    ");
            for (int x = 0; x < m.W; x++) sb.Append((char)('0' + x % 10));
            sb.AppendLine();
            for (int y = m.H - 1; y >= 0; y--)
            {
                sb.Append(y.ToString().PadLeft(3)).Append(' ');
                for (int x = 0; x < m.W; x++) sb.Append(g[m.Idx(x, y)]);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static JObj Rules()
        {
            var o = new JObj();
            o.Set("overview", "Real-time strategy. Harvesters collect ore ($) and unload at a refinery for credits. Spend credits to build structures (from the construction yard) and units (barracks: infantry, war factory: vehicles). A team is defeated when it has no structures left. Game runs at 20 ticks/second in real time; it does not wait for you.");
            o.Set("tips", new List<string> {
                "Typical opening: power_plant -> refinery -> barracks -> war_factory, then more refineries/harvesters.",
                "Keep power produced >= power used or production halves.",
                "Rockets beat vehicles, rifles beat infantry, tanks are all-round. Heavy tanks splash.",
                "Use attack_move to send armies; units fight what they meet. Use 'all' or 'idle' for units.",
                "Omit x,y on build to auto-place near your base.",
                "Enemy starting positions are known; their bases are fogged until you scout them.",
            });
            o.Set("structures", Defs.All.Values.Where(d => d.IsStructure).Select(DefJson).ToList());
            o.Set("units", Defs.All.Values.Where(d => !d.IsStructure).Select(DefJson).ToList());
            o.Set("commands", Commands.Help);
            return o;
        }

        static JObj DefJson(EntityDef d)
        {
            var o = new JObj().Set("key", d.Key).Set("cost", d.Cost).Set("build_time_s", d.BuildTime).Set("hp", d.MaxHp).Set("armor", d.Armor.ToString().ToLowerInvariant());
            if (d.IsStructure) o.Set("size", $"{d.SizeX}x{d.SizeY}").Set("power", d.Power);
            else o.Set("speed", d.Speed);
            if (d.Weapon != null) o.Set("weapon", $"{d.Weapon.Name}: {d.Weapon.Damage} dmg, range {d.Weapon.Range}, every {d.Weapon.Cooldown}s; x{d.Weapon.VsInfantry} vs infantry, x{d.Weapon.VsVehicle} vs vehicles, x{d.Weapon.VsStructure} vs structures");
            var req = new List<string>();
            if (d.BuiltBy == Producer.Barracks) req.Add("barracks");
            if (d.BuiltBy == Producer.WarFactory) req.Add("war_factory");
            if (d.BuiltBy == Producer.ConstructionYard) req.Add("construction_yard");
            req.AddRange(d.Requires);
            o.Set("requires", req);
            o.Set("description", d.Description);
            return o;
        }
    }
}
