using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>The join flow (docs/JOIN_FLOW.md): rooms that wait for players, the sudden-death join cutoff, and the lobby's read on a room.</summary>
    public static partial class Tests
    {
        static void JoinFlow()
        {
            Console.WriteLine("\n-- join flow");
            WaitingRoom();
            Sparring();
            LobbyAndCutoff();
        }

        static Game WaitingGame(int minPlayers, Func<DateTime> now)
        {
            var g = new Game(new GameConfig { Seed = 9, MapSize = 64, Open = true, Controllers = new[] { "ai" }, HouseAIs = 1, MinPlayers = minPlayers });
            g.Now = now;
            return g;
        }

        static void WaitingRoom()
        {
            var clock = default(DateTime);
            var game = WaitingGame(2, () => clock);
            clock = DateTime.UtcNow; // the test's clock from here: the room began waiting a moment before
            var w = game.World;
            Check(game.Waiting && w.Teams.Count == 0, $"a room with min_players starts waiting, with no house AI ({w.Teams.Count} seats)");
            Step(game, 30);
            Check(w.Tick == 0, "nothing advances while it waits");

            var a = game.Join("llm", "Ada", null, out _, out _);
            Check(game.Waiting && game.OutsidePlayers == 1 && a.ProtectedUntil == 0 && a.Amount("steel") == 0 && w.Owned(a.Id).Count(e => e.IsStructure) == 1,
                  "the first agent is seated as a founder: still waiting, no newcomer protection, no catch-up kit");
            var r = game.Execute(a.Id, Cmd("type", "build", "structure", "power_plant"));
            Check(!Ok(r) && r["error"].ToString().StartsWith("waiting_for_players: waiting for players: 1 of 2 seated"), $"commands are refused while it waits: {r["error"]}");
            Check(Ok(game.Execute(a.Id, Cmd("type", "say", "text", "anyone?"))), "say still works");
            r = game.Execute(a.Id, Cmd("type", "start_match", "opponent", "scripted"));
            Check(!Ok(r) && r["error"].ToString().Contains("300s to go"), $"start_match isn't open before 5 minutes: {r["error"]}");
            a.LastActiveAt = -2 * World.IdleResignAfter;
            Step(game, 5);
            Check(!a.Left, "idle resign doesn't fire while it waits (no game time passes)");
            a.LastActiveAt = 0;

            clock = clock.AddSeconds(120);
            Check(game.WaitedSeconds >= 119.9f && game.WaitedSeconds < 121, $"it counts real seconds waited ({game.WaitedSeconds:0.0})");
            var json = Snap(game);
            var back = Resume(json);
            Check(back.Waiting && back.Config.MinPlayers == 2 && back.World.Teams.Count == 1 && back.WaitedSeconds >= 119.9f && back.WaitedSeconds < 125,
                  $"a saved waiting room resumes waiting, with min_players and the time waited ({back.WaitedSeconds:0.0}s)");
            back.Now = () => clock;
            game = back; w = back.World; a = w.Teams[0];

            var b = game.Join("llm", "Bo", null, out _, out _);
            Check(!game.Waiting && b.ProtectedUntil == 0 && a.ProtectedUntil == 0 && w.Time == 0, "the second agent starts the match at t=0, with both founders unprotected");
            Check(w.Events.Any(e => e.Type == "chat" && e.Text.StartsWith("The match has started")) && w.Alerts.Active(w, a.Id).Any(a1 => a1.Kind == "match_clock") && w.Alerts.Active(w, b.Id).Any(a1 => a1.Kind == "match_clock"),
                  "a chat line and an alert to each player say it has started");
            Check(Ok(game.Execute(a.Id, Cmd("type", "build", "structure", "power_plant"))), "commands work now");
            Step(game, 60);
            Check(w.Time >= 59, $"and the clock runs ({w.Time:0}s)");
            var c = game.Join("llm", "Cy", null, out _, out _);
            Check(c.ProtectedUntil > w.Time && c.Amount("steel") > 0, "a player who joins after the start gets protection and the catch-up kit, as today");

            game.NextMatch();
            Check(game.Waiting && game.World.Teams.Count == 0 && game.Config.MinPlayers == 2 && game.Config.MatchHours == 4, "the next match opens waiting too, with the same min_players and match length");
            Check(w.Errors == 0 && game.World.Errors == 0, $"no sim errors ({w.LastError})");
        }

        static void Sparring()
        {
            var clock = default(DateTime);
            var game = WaitingGame(2, () => clock);
            clock = DateTime.UtcNow;
            var w = game.World;
            var a = game.Join("llm", "Solo", null, out _, out _);
            clock = clock.AddSeconds(299);
            var r = game.Execute(a.Id, Cmd("type", "start_match", "opponent", "scripted"));
            Check(!Ok(r) && r["error"].ToString().Contains("1s to go"), $"at 4:59 start_match still says how long is left: {r["error"]}");
            clock = clock.AddSeconds(2);
            Check(!Ok(game.Execute(a.Id, Cmd("type", "start_match"))), "it needs opponent scripted");
            Step(game, 0.1f);
            var choice = w.Alerts.Active(w, a.Id).FirstOrDefault(x => x.Kind == "waiting_choice");
            Check(choice != null && AlertLog.Describe(w, choice).Contains("start_match"), "at 5 minutes alone an alert spells out the choice");
            Check(game.WaitingText().Contains("{\"type\":\"start_match\",\"opponent\":\"scripted\"}"), "and so does the waiting text in state");
            r = game.Execute(a.Id, Cmd("type", "start_match", "opponent", "scripted"));
            var ai = w.Teams.FirstOrDefault(t => t.Controller == "ai");
            var savedAi = ((List<object>)((JObj)Snapshot.Write(game)["game"])["ais"]).OfType<JObj>().FirstOrDefault();
            Check(Ok(r) && !game.Waiting && ai != null && ai.House && ai.ProtectedUntil == 0 && game.OutsidePlayers == 1 && savedAi != null && !savedAi.Contains("passive"),
                  $"after 5 minutes it starts against the scripted AI: seated as house, not counted, and aggressive: {r["result"]}");
            Check(!Ok(game.Execute(a.Id, Cmd("type", "start_match", "opponent", "scripted"))), "start_match again: the match is already running");

            var three = WaitingGame(3, () => clock);
            var p1 = three.Join("llm", "One", null, out _, out _); three.Join("llm", "Two", null, out _, out _);
            clock = clock.AddSeconds(400);
            r = three.Execute(p1.Id, Cmd("type", "start_match", "opponent", "scripted"));
            Check(three.Waiting && !Ok(r) && r["error"].ToString().Contains("2 players are seated"), $"start_match is only for a player waiting alone: {r["error"]}");
        }

        /// <summary>Over HTTP: register and wait in a waiting room, the lobby's fields in every phase, and the sudden-death cutoff.</summary>
        static void LobbyAndCutoff()
        {
            var game = WaitingGame(2, () => DateTime.UtcNow);
            var api = new ApiServer(7985);
            api.Start();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // One thread, as in IdleOverHttp. The reply's HTTP status rides along as "_status".
            Dictionary<string, object> Call(System.Threading.Tasks.Task<HttpResponseMessage> req)
            {
                while (!req.IsCompleted) { game.Advance(0.01f); api.Pump(game); Thread.Sleep(10); }
                var d = (Dictionary<string, object>)Json.Parse(req.Result.Content.ReadAsStringAsync().Result);
                d["_status"] = (double)(int)req.Result.StatusCode;
                return d;
            }
            const string Url = "http://127.0.0.1:7985";
            Dictionary<string, object> Lobby() => Call(http.GetAsync($"{Url}/api/lobby"));
            Dictionary<string, object> Register(string name) => Call(http.PostAsync($"{Url}/api/register", new StringContent($"{{\"name\":\"{name}\"}}")));

            var l = Lobby();
            Check(l.Str("status") == "waiting" && l["joinable"] is bool j0 && j0 && l["why"] == null && l.Str("verdict") == "fresh" && l["age_s"] == null && l["ends_in_s"] == null
                  && l["sudden_death_in_s"] == null && (double)l["min_players"] == 2 && l["waiting_s"] is double && (double)l["seats_free"] == 8 && (double)l["match_hours"] == 4,
                  $"lobby, waiting: status, joinable, fresh, no clock yet, min_players and waiting_s ({l.Str("verdict_text")})");

            var ra = Register("Ada");
            var wa = ra["waiting"] as Dictionary<string, object>;
            Check(ra.Str("room_status") == "waiting" && wa != null && (double)wa["players"] == 1 && (double)wa["min_players"] == 2 && wa["waited_s"] is double && (double)wa["choose_after_s"] > 290,
                  "register in a waiting room says so: room_status and waiting {players, min_players, waited_s, choose_after_s}");
            var tok = ra.Str("token");
            var st = Call(http.GetAsync($"{Url}/api/state?token={tok}"));
            Check(st.Keys.First() == "waiting" && st.Str("waiting").StartsWith("waiting for players: 1 of 2"), "state says first what it's waiting for");
            var stj = Call(http.GetAsync($"{Url}/api/state?format=json&token={tok}"));
            Check(stj["waiting"] is Dictionary<string, object> wj && (double)wj["players"] == 1, "and so does state format=json");
            var cmd = Call(http.PostAsync($"{Url}/api/command?token={tok}", new StringContent("[{\"type\":\"train\",\"unit\":\"rifleman\"}]")));
            Check(((List<object>)cmd["results"]).OfType<Dictionary<string, object>>().First().Str("error").StartsWith("waiting_for_players:"), "a command over HTTP is refused with waiting_for_players");

            // A wait while alone comes back when the second player joins (and the match starts), not at its deadline.
            var waitReq = http.GetAsync($"{Url}/api/wait?seconds=30&token={tok}");
            var until = DateTime.UtcNow.AddSeconds(0.3);
            while (DateTime.UtcNow < until) { api.Pump(game); Thread.Sleep(10); }
            var rb = Register("Bo");
            var waited = Call(waitReq);
            Check(rb.Str("room_status") == "playing" && !rb.ContainsKey("waiting") && waited["interrupted"] is bool wi && wi && (double)waited["waited_s"] < 5,
                  $"the second player starts the match, and the first one's wait returns early ({waited["waited_s"]}s)");

            var w = game.World;
            l = Lobby();
            var teams = ((List<object>)l["teams"]).OfType<Dictionary<string, object>>().ToList();
            Check(l.Str("status") == "playing" && l.Str("verdict") == "fresh" && (double)l["age_s"] < 5 && (double)l["sudden_death_in_s"] > 4 * 3600 - 5 && !l.ContainsKey("min_players") && (double)l["seats_free"] == 6,
                  $"lobby, just started: playing, fresh, the clock and seats ({l.Str("verdict_text")})");
            Check(teams.Count == 2 && teams.All(t => t.Str("team_name") == t.Str("flavor") && t["score"] is double), "each team has its team_name and score");
            Check(System.Text.RegularExpressions.Regex.IsMatch(l.Str("verdict_text"), @"^(4h00m|3h59m) to sudden death; 2 active players; the leader \(\w+\) has \d+% of the points\.$"), $"verdict_text: {l.Str("verdict_text")}");

            void At(float seconds) => w.Tick = (int)(seconds * World.TickRate);
            At(30 * 60); Check(Lobby().Str("verdict") == "early", "at 30 minutes of 4 h: early");
            At(2 * 3600); Check(Lobby().Str("verdict") == "midgame", "at 2 h: midgame");
            At(4 * 3600 - 20 * 60); l = Lobby();
            Check(l.Str("verdict") == "late" && l.Str("status") == "playing" && l["joinable"] is bool j1 && j1, "20 minutes before sudden death: late, but still joinable");

            At(4 * 3600 + 60); l = Lobby();
            Check(l.Str("status") == "closing" && l["joinable"] is bool j2 && !j2 && l.Str("why") == "sudden death: closed to new players" && l.Str("verdict") == "late",
                  $"in sudden death: closing, not joinable ({l.Str("why")})");
            var refused = Register("Late");
            Check((double)refused["_status"] == 409 && refused["ok"] is bool ok0 && !ok0 && refused.Str("code") == "room_closed_to_joins" && refused.Str("error").StartsWith("room_closed_to_joins: ")
                  && refused["next_match_in_s"] is double nm && Math.Abs(nm - (3600 - 60 + Game.ResultsSeconds)) < 2,
                  $"register in sudden death: 409 room_closed_to_joins with next_match_in_s ({refused["next_match_in_s"]}): {refused.Str("error")}");

            At(w.MatchEndsAt); w.GameOver = true; l = Lobby();
            Check(l.Str("status") == "results" && l.Str("why") == $"match over: results showing; next match in {Game.ResultsSeconds:0}s" && l.Str("verdict") == "late" && (double)l["ends_in_s"] == 0,
                  $"match over: results ({l.Str("why")}; {l.Str("verdict_text")})");
            refused = Register("Later");
            Check((double)refused["_status"] == 409 && refused.Str("code") == "room_closed_to_joins" && refused["next_match_in_s"] is double nm2 && nm2 == Game.ResultsSeconds, "register at the results: 409 with the countdown");

            // A full room isn't joinable either.
            var full = new Game(new GameConfig { Seed = 9, MapSize = 64, Open = true, Controllers = new[] { "ai" }, MaxPlayers = 2 });
            full.Join("llm", "Ivy", null, out _, out _);
            Check(full.WhyNotJoinable() == "all 2 seats taken" && full.World.SeatsFree == 0, $"a full room: {full.WhyNotJoinable()}");
            api.Stop();
        }
    }
}
