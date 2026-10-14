using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public sealed class CommandReceipt
    {
        public string Id, Reply;
        public DateTime At;
    }

    public partial class World
    {
        /// <summary>Only while a command executes; automatic orders otherwise identify themselves as sim.</summary>
        public string CommandActor;
    }

    /// <summary>API-equivalent batches, with per-seat retry receipts and advisory stale facts. No tactical choices.</summary>
    public static class CommandSafety
    {
        public const int MaxReceipts = 512, MaxGoneUnits = 2048;
        public const double ReceiptSeconds = 600;

        public static JObj ExecuteBatch(Game game, int team, object parsed, string actor, Action<int, string, string> onCommand = null)
        {
            var w = game.World;
            if (team < 0 || team >= w.Teams.Count) return Error("bad team");
            var t = w.Teams[team];
            var envelope = parsed as Dictionary<string, object>;
            var commands = parsed as List<object>;
            object raw = null;
            bool batch = envelope != null && envelope.TryGetValue("commands", out raw);
            if (batch)
            {
                commands = raw as List<object>;
                if (commands == null) return Error("commands must be an array");
            }
            commands ??= new List<object> { parsed };
            string request = null;
            int? asOf = null;
            if (batch && !Metadata(envelope, out request, out asOf, out var error)) return Error(error);
            var now = game.Now();
            t.CommandReceipts.RemoveAll(r => (now - r.At).TotalSeconds >= ReceiptSeconds);
            var key = request == null ? null : "batch:" + request;
            var saved = Recall(t, key);
            if (saved != null) return saved;
            // Validate before running any part of a new batch. Existing retry keys always return their first result.
            foreach (var c in commands.OfType<Dictionary<string, object>>())
                if (!Metadata(c, out _, out _, out var commandError)) return Error(commandError);
            var newKeys = new HashSet<string>();
            if (key != null) newKeys.Add(key);
            foreach (var c in commands.OfType<Dictionary<string, object>>())
                if (c.TryGetValue("request_id", out var r) && !t.CommandReceipts.Any(x => x.Id == "command:" + r)) newKeys.Add("command:" + r);
            if (t.CommandReceipts.Count + newKeys.Count > MaxReceipts) return Error("retry receipt capacity reached; retry with a new request after an older receipt expires").Set("code", "receipt_capacity");
            var results = new List<object>();
            foreach (var c in commands)
            {
                if (!(c is Dictionary<string, object> command)) { results.Add(Error("each command must be an object")); continue; }
                Metadata(command, out var id, out var tick, out _);
                var commandKey = id == null ? null : "command:" + id;
                var result = Recall(t, commandKey);
                if (result == null)
                {
                    var facts = StaleFacts(w, team, command, tick ?? asOf);
                    var previousActor = w.CommandActor;
                    try
                    {
                        w.CommandActor = actor;
                        result = w.Observe.Track(team, command, () => Obsolete(w, team, command) ?? game.Execute(team, command));
                    }
                    finally { w.CommandActor = previousActor; }
                    if ((tick ?? asOf).HasValue) result.Set("as_of_tick", tick ?? asOf).Set("stale", facts);
                    if (id != null) result.Set("request_id", id);
                    if (result["ok"] is bool ok && ok) w.LogCommand(team, command);
                    onCommand?.Invoke(team, Json.Write(command), Json.Write(result));
                    Remember(t, commandKey, result, now);
                }
                results.Add(result);
            }
            var reply = new JObj().Set("results", results).Set("stockpile", StateView.Stockpile(t)).Set("time_s", (float)Math.Round(w.Time, 1));
            if (request != null) reply.Set("request_id", request);
            Remember(t, key, reply, now);
            return reply;
        }

        static bool Metadata(Dictionary<string, object> d, out string request, out int? tick, out string error)
        {
            request = null; tick = null; error = null;
            if (d.TryGetValue("request_id", out var r))
            {
                if (!(r is string s) || s.Length < 1 || s.Length > 128) { error = "request_id must be a string of 1-128 characters"; return false; }
                request = s;
            }
            if (d.TryGetValue("as_of_tick", out var a))
            {
                if (!(a is double n) || double.IsNaN(n) || double.IsInfinity(n) || n < 0 || n > int.MaxValue || n != Math.Floor(n))
                { error = "as_of_tick must be a non-negative integer"; return false; }
                tick = (int)n;
            }
            return true;
        }

        static JObj Recall(Team t, string id)
        {
            if (id == null) return null;
            var r = t.CommandReceipts.FirstOrDefault(x => x.Id == id);
            if (r == null) return null;
            var reply = new JObj();
            foreach (var kv in (Dictionary<string, object>)Json.Parse(r.Reply)) reply.Set(kv.Key, kv.Value);
            return reply;
        }

        static void Remember(Team t, string id, JObj reply, DateTime now)
        {
            if (id == null) return;
            t.CommandReceipts.Add(new CommandReceipt { Id = id, Reply = Json.Write(reply), At = now });

        }

        static List<object> StaleFacts(World w, int team, Dictionary<string, object> c, int? asOf)
        {
            var facts = new List<object>();
            if (!asOf.HasValue) return facts;
            var ids = c.Ids("units");
            if (c.TryGetValue("units", out var selector) && selector is string s && (s == "all" || s == "idle"))
                ids = w.Owned(team).Where(e => !e.IsStructure && e.IsArmed && (s != "idle" || e.Order == Order.Idle)).Select(e => e.Id).ToList();
            foreach (int id in ids.Distinct())
            {
                var e = w.Get(id);
                if (e != null && e.Team == team)
                {
                    var changes = new List<object>();
                    if (e.LastOrderTick > asOf) changes.Add(new JObj().Set("kind", "ordered").Set("tick", e.LastOrderTick).Set("by", e.LastOrderBy).Set("order", e.Order.ToString().ToLowerInvariant()));
                    if (e.LastRetreatTick > asOf) changes.Add(new JObj().Set("kind", "retreated").Set("tick", e.LastRetreatTick));
                    if (changes.Count > 0) facts.Add(new JObj().Set("unit", id).Set("changes", changes));
                }
                else if (w.Teams[team].GoneUnits.TryGetValue(id, out var gone) && gone > asOf)
                    facts.Add(new JObj().Set("unit", id).Set("changes", new List<object> { new JObj().Set("kind", "destroyed").Set("tick", gone) }));
            }
            return facts;
        }

        static JObj Obsolete(World w, int team, Dictionary<string, object> c)
        {
            string type = c.Str("type", "").ToLowerInvariant();
            string field = type == "load" ? "transport" : "target";
            if (!(new[] { "attack", "capture", "repair", "heal", "load", "refuel" }).Contains(type) || !c.TryGetValue(field, out var value) || !(value is double target) || target <= 0) return null;
            var entity = w.Get((int)target);
            if (entity != null && (entity.Team == team || w.IsVisibleTo(team, entity) || entity.Def.Key == "derrick")) return null;
            // Absence is not proof of an enemy death. Never consult hidden enemy tombstones.
            return Error("the target is no longer available; read your current state before choosing another")
                .Set("code", "obsolete").Set("why", "target_unavailable").Set("target", (int)target);
        }

        static JObj Error(string message) => new JObj().Set("ok", false).Set("error", message);
    }
}
