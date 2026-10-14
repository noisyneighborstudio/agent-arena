using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    public static partial class Tests
    {
        static void ExecutorEventPrivacy()
        {
            Console.WriteLine("\n-- private executor decisions on visible ground");
            var game = new Game(new GameConfig { Seed = 5, MapSize = 64, Controllers = new[] { "llm", "llm" } });
            var w = game.World;
            Array.Fill(w.Teams[0].Visible, true);
            var at = w.Teams[0].StartPos;
            var kinds = new[] { "miners_withdrawn", "miners_resumed", "guard_triggered", "guard_released", "detached_by_order", "decision_needed" };
            foreach (string kind in kinds)
            {
                w.Emit(kind, 1, pos: at, text: "private-rival-" + kind);
                w.Emit(kind, 0, pos: at, text: "own-" + kind);
                w.Observe.Note(1, "orders", kind, Sev.Critical, "own", new JObj().Set("reason", "private-rival-" + kind), "rival decision");
            }
            var player = Json.Write(ApiServer.ViewSnapshot(game, 0));
            var ownState = Json.Write(StateData.Team(w, 0));
            var feed = Json.Write(w.Observe.Log(0).Read(null, -1, new ObsFilter(), 4000));
            var spectator = Json.Write(ApiServer.ViewSnapshot(game));
            Check(!player.Contains("private-rival-") && kinds.All(kind => player.Contains("own-" + kind)),
                "a player mirror keeps own decisions but hides rival executor facts even where terrain is visible");
            Check(!ownState.Contains("private-rival-") && kinds.All(kind => ownState.Contains("own-" + kind)),
                "structured state exposes only the team's own executor explanations");
            Check(!feed.Contains("private-rival-"), "a rival's critical decision never enters this team's observation stream");
            Check(kinds.All(kind => spectator.Contains("private-rival-" + kind)),
                "the full spectator snapshot retains decisions for the existing delayed gateway buffer");
        }
    }
}
