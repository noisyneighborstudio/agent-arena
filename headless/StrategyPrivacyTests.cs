using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static void StrategyPlanningPrivacy()
        {
            var game = new Game(new GameConfig { Seed = 5, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            var w = game.World;
            Array.Fill(w.Teams[0].Visible, true);
            foreach (int team in new[] { 0, 1 })
            {
                string side = team == 0 ? "own" : "rival";
                var strategy = Cmd("type", "set_strategy", "text", side + "-strategy-marker\n<size=100>literal prose</size>",
                    "priorities", new List<object> { side + "-priority-marker" });
                CommandSafety.ExecuteBatch(game, team, new List<object> { strategy }, "privacy-test");
                w.SetOrders(team, side + "-private-standing-orders");
                var unit = w.Owned(team).First(e => !e.IsStructure && !e.IsMine);
                Commands.Execute(w, team, Cmd("type", "squad", "name", side, "add", new[] { unit.Id }));
                Commands.Execute(w, team, Cmd("type", "guard", "squad", side, "retreat_below_pct", 40,
                    "to", new List<object> { (double)w.Teams[team].StartPos.X, (double)w.Teams[team].StartPos.Y }));
                w.Teams[team].Squads[side].Guards.Notes.Add(Json.Write(new JObj().Set("rule", "retreat_below_pct")
                    .Set("reason", side + "-intervention-marker").Set("t", w.Time)));
                Commands.Execute(w, team, Cmd("type", "objective", "id", side + "-objective-marker", "squad", side,
                    "verb", "hold", "at", new List<object> { (double)unit.Pos.X, (double)unit.Pos.Y }, "radius", 6));
            }

            var own = Json.Write(ApiServer.ViewSnapshot(game, 0));
            var spectator = Json.Write(ApiServer.ViewSnapshot(game));
            var markers = new[] { "strategy-marker", "priority-marker", "objective-marker", "intervention-marker" };
            Check(markers.All(m => own.Contains("own-" + m)) && markers.All(m => !own.Contains("rival-" + m)),
                "live player snapshots retain own planning and remove rival planning even on visible ground");
            Check(own.Contains("own-private-standing-orders") && !own.Contains("rival-private-standing-orders"),
                "live planning preserves only the player's private human standing orders");
            Check(markers.All(m => spectator.Contains("own-" + m) && spectator.Contains("rival-" + m)) &&
                  !spectator.Contains("private-standing-orders"),
                "spectator snapshots retain both plans for the delayed buffer while human instructions stay private");

            var liveWorld = ((Dictionary<string, object>)Json.Parse(own)).Obj("world");
            var liveRival = liveWorld.Objs("teams").First(t => t.In("id") == 1);
            Check(!liveRival.ContainsKey("strategy_text") && !liveRival.ContainsKey("priorities") && !liveRival.ContainsKey("plan_version"),
                "a live rival's plan fields are absent rather than exposing empty or version-only metadata");
            var restored = Resume(own);
            Check(restored.World.Teams[0].StrategyText == w.Teams[0].StrategyText && restored.World.Teams[0].PlanVersion == w.Teams[0].PlanVersion &&
                  restored.World.Teams[1].StrategyText == "" && restored.World.Teams[1].Priorities.Count == 0,
                "a rendered mirror restores its own plan without reconstructing a rival's strategy");

            var liveFrame = Json.Write(ApiServer.ViewFrame(w, 0));
            var watchFrame = Json.Write(ApiServer.ViewFrame(w, -1));
            Check(markers.All(m => liveFrame.Contains("own-" + m)) && markers.All(m => !liveFrame.Contains("rival-" + m)),
                "the classic live viewer frame exposes only its owner's planning");
            Check(markers.All(m => watchFrame.Contains("own-" + m) && watchFrame.Contains("rival-" + m)) &&
                  !watchFrame.Contains("private-standing-orders"),
                "the classic spectator frame carries both plans and keeps human instructions private");
        }
    }
}
