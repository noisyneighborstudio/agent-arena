using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static void TurnSafety()
        {
            Console.WriteLine("\n-- command retry and stale decisions");
            var game = new Game(new GameConfig { Seed = 19, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            var now = DateTime.UtcNow;
            game.Now = () => now;
            var w = game.World;
            var t = w.Teams[0];
            foreach (var team in w.Teams) team.ProtectedUntil = 0;
            t.Stock["steel"] = 10000;
            w.SpawnStructure(0, "barracks", w.FindPlacement(0, "barracks").Value, 1f);
            Check(w.HasComplete(0, "barracks"), "retry fixture has a barracks");
            int callbacks = 0;
            var batch = Cmd("request_id", "buy-once", "commands", new List<object> { Cmd("type", "train", "unit", "rifleman", "count", 2) });
            JObj Send(object c, string actor = "Ada") => CommandSafety.ExecuteBatch(game, 0, c, actor, (_, __, ___) => callbacks++);
            var first = Send(batch);
            string reply = Json.Write(first);
            int stock = t.Amount("steel"), queued = t.UnitQueues.Values.Sum(q => q.Count);
            long replay = w.CommandSeq;
            long head = w.Observe.Log(0).Head;
            w.Tick += World.TickRate;
            var retry = Send(batch);
            Check(Json.Write(retry) == reply && t.Amount("steel") == stock && t.UnitQueues.Values.Sum(q => q.Count) == queued && callbacks == 1 && w.CommandSeq == replay && w.Observe.Log(0).Head == head,
                "a batch retry returns the original reply, without spending, tracking, replay or callbacks again");
            var back = Resume(Snap(game));
            back.Now = () => now;
            Check(Json.Write(CommandSafety.ExecuteBatch(back, 0, batch, "Ada")) == reply, "a receipt survives a full snapshot and engine restart");
            now = now.AddSeconds(601);
            Send(batch);
            Check(callbacks == 2 && t.Amount("steel") < stock, "a retry key expires after ten real minutes");
            var single = Cmd("type", "say", "text", "once", "request_id", "single");
            Send(single); Send(new List<object> { single });
            Check(w.Events.Count(e => e.Type == "chat" && e.Text == "once") == 1, "per-command keys work in single commands and arrays");
            var other = CommandSafety.ExecuteBatch(game, 1, single, "Bo");
            Check(w.Events.Count(e => e.Type == "chat" && e.Text == "once") == 2, "request keys are isolated per team");
            int before = callbacks;
            var invalid = Send(Cmd("commands", new List<object> { Cmd("type", "say", "text", "partial"), Cmd("type", "say", "text", "bad", "request_id", 8) }));
            Check(!Ok(invalid) && callbacks == before && !w.Events.Any(e => e.Text == "partial"), "malformed metadata is refused before any batch side effect");

            var unit = w.Owned(0).First(e => !e.IsStructure && e.IsArmed);
            int asOf = w.Tick;
            w.Tick++;
            Send(Cmd("type", "move", "units", new[] { unit.Id }, "x", unit.Pos.X + 4, "y", unit.Pos.Y), "human commander");
            w.Tick++;
            var stale = Send(Cmd("commands", new List<object> { Cmd("type", "stop", "units", new[] { unit.Id }) }, "as_of_tick", asOf));
            var staleResult = (JObj)((List<object>)stale["results"])[0];
            Check(Ok(staleResult) && Json.Write(staleResult["stale"]).Contains("human commander") && unit.Order == Order.Idle,
                "stale orders still execute and identify the intervening commander");
            unit.LastRetreatTick = w.Tick;
            unit.LastOrderTick = w.Tick;
            unit.LastOrderBy = "sim";
            w.Tick++;
            var retreated = Send(Cmd("type", "stop", "units", new[] { unit.Id }, "as_of_tick", asOf));
            Check(Json.Write(retreated).Contains("retreated"), "a retreat after the decision tick is reported even if it ended");
            int dead = unit.Id;
            w.Remove(unit);
            w.Tick++;
            var loss = Send(Cmd("type", "stop", "units", new[] { dead }, "as_of_tick", asOf));
            Check(Json.Write(loss).Contains("destroyed"), "own destroyed units named in a stale command are reported");
            back = Resume(Snap(game)); back.Now = () => now;
            Check(back.World.Teams[0].GoneUnits.ContainsKey(dead), "own unavailable-unit facts survive snapshot collection of dead entities");
            var live = w.Owned(0).First(e => !e.IsStructure);
            Check(back.World.Get(live.Id).LastOrderTick == live.LastOrderTick && back.World.Get(live.Id).LastOrderBy == live.LastOrderBy,
                "last order tick and provenance survive a snapshot");
            var obsolete = Send(Cmd("type", "attack", "units", new[] { live.Id }, "target", dead));
            Check(Json.Write(obsolete).Contains("obsolete") && Json.Write(obsolete).Contains("target_unavailable"), "missing targets return obsolete and a reason");
            // Enemy deaths never populate another team's unavailable-own-unit records.
            var enemy = w.Owned(1).First(e => !e.IsStructure);
            var hiddenTarget = Send(Cmd("type", "attack", "units", new[] { live.Id }, "target", enemy.Id));
            w.Remove(enemy);
            var goneTarget = Send(Cmd("type", "attack", "units", new[] { live.Id }, "target", enemy.Id));
            Check(Json.Write(hiddenTarget).Contains("target_unavailable") && Json.Write(goneTarget).Contains("target_unavailable"), "hidden live and unseen gone targets have the same unavailable reason");
            var fog = Send(Cmd("type", "stop", "units", new[] { enemy.Id }, "as_of_tick", 0));
            Check(!Json.Write(fog).Contains("destroyed"), "stale facts never disclose an enemy loss");
            var viewed = Json.Write(ApiServer.ViewSnapshot(game, 0));
            Check(!viewed.Contains("command_receipts") && !viewed.Contains("gone_units") && !viewed.Contains("buy-once"), "retry receipts and unavailable-unit history stay out of viewer copies");
            for (int i = 0; i < CommandSafety.MaxReceipts + 2; i++) Send(Cmd("type", "say", "text", "bounded", "request_id", "bound-" + i));
            var overflow = Send(Cmd("type", "say", "text", "over-capacity", "request_id", "overflow"));
            Check(t.CommandReceipts.Count == CommandSafety.MaxReceipts && !Ok(overflow) && !w.Events.Any(e => e.Text == "over-capacity") && Json.Write(Send(single)).Contains("single"), "capacity refuses new retry keys without evicting unexpired replies or executing side effects");
        }
    }
}
