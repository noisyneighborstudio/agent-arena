using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Pez.Api;
using Pez.Sim;

namespace Pez.Headless
{
    /// <summary>Agents name their own team when they join; the colour stays with the seat.</summary>
    public static partial class Tests
    {
        static void TeamNames()
        {
            // Cleaning: whitespace collapsed, odd characters dropped, at most 24 characters, something readable left.
            Check(Text.TeamName("  Red   Fox\t\n Club  ") == "Red Fox Club", $"team names collapse whitespace: '{Text.TeamName("  Red   Fox\t\n Club  ")}'");
            Check(Text.TeamName("Evil‮Name <b>!</b>") == "EvilName bb", $"control, bidi and markup characters are stripped: '{Text.TeamName("Evil‮Name <b>!</b>")}'");
            var longName = Text.TeamName("The Quick Brown Fox Jumps Over The Lazy Dog");
            Check(longName.Length <= 24 && !longName.EndsWith(" "), $"long team names are cut to 24 characters: '{longName}'");
            Check(Text.TeamName("!!! ???") == null && Text.TeamName("\U0001F525") == null && Text.TeamName("") == null && Text.TeamName(null) == null, "a name with nothing readable in it is no name");

            var w = new World(1, 7, 64) { Open = true, MaxPlayers = 8, MaxMapSize = 320 };
            var a = w.AddTeam("llm", "Claude", "Ember Fox", out var err, out var problem);
            Check(a != null && a.Name == "Ember Fox" && problem == null && a.PlayerName == "Claude", $"a joiner's team name is applied ({a?.Name}, player {a?.PlayerName})");
            Check(w.Events.Any(e => e.Type == "joined" && e.Text.Contains("joined as Ember Fox")), "the join announcement uses the team name");
            Check(a.Id == 1 && World.Flavors[a.Id] == "Cherry", "the seat (and so the colour) is unchanged by the name");

            // Uniqueness ignores case, spaces and punctuation, and covers the other seats' default names.
            var b = w.AddTeam("llm", "Codex", "EMBER-fox", out err, out problem);
            Check(b != null && b.Name == World.Flavors[b.Id] && problem != null && problem.Contains("already"), $"a taken name is refused and the team keeps its flavour ({b?.Name}: {problem})");
            var c = w.AddTeam("llm", "Gemini", "Plum", out err, out problem);
            Check(c != null && c.Name == World.Flavors[c.Id] && problem != null, $"another seat's default name is refused ({c?.Name}: {problem})");
            var d = w.AddTeam("llm", "Grok", "arena", out err, out problem);
            Check(d != null && d.Name == World.Flavors[d.Id] && problem != null, $"a name that could pass for the game itself is refused ({problem})");
            var e = w.AddTeam("llm", "Zed", "!!!", out err, out problem);
            Check(e != null && e.Name == World.Flavors[e.Id] && problem != null, $"a name with nothing readable falls back to the flavour ({problem})");
            var f = w.AddTeam("llm", "Cursor", "Lemon", out err, out problem); // Lemon is seat 3's
            Check(f != null && f.Name == World.Flavors[f.Id] && problem != null, $"so does a flavour that belongs to a different seat ({problem})");
            var own = w.AddTeam("llm", "Amp", World.Flavors[w.Teams.Count], out err, out problem);
            Check(own != null && problem == null && own.Name == World.Flavors[own.Id], "a seat can pick its own flavour as its name");
            Check(w.Teams.Select(t => t.Name.ToLowerInvariant()).Distinct().Count() == w.Teams.Count, "no two teams share a name");

            // The name is in everything that prints it.
            var lobby = Json.Write(new JObj().Set("teams", w.Teams.Select(t => t.Name).ToList()));
            Check(lobby.Contains("Ember Fox"), "teams list the chosen name");

            // Saved games keep it.
            var game = new Game(new GameConfig { Seed = 3, MapSize = 64, Open = true, Controllers = new[] { "ai" }, HouseAIs = 1 });
            var g1 = game.World.AddTeam("llm", "Claude", "Nimbus Crew", out err, out problem);
            var g2 = game.World.AddTeam("llm", "Codex", null, out err, out problem);
            Step(game, 2);
            var json = Snap(game);
            var resumed = Resume(json);
            Check(resumed.World.Teams[g1.Id].Name == "Nimbus Crew" && resumed.World.Teams[g2.Id].Name == World.Flavors[g2.Id],
                  $"team names survive a save and resume ({resumed.World.Teams[g1.Id].Name}, {resumed.World.Teams[g2.Id].Name})");
            Check(Snap(resumed) == json, "and the resumed game saves back byte for byte");
            var dup = resumed.World.AddTeam("llm", "Late", "nimbus crew", out err, out problem);
            Check(dup != null && problem != null, "uniqueness still holds after a resume");

            // A seat that changes hands starts over: the old occupant's name goes with them.
            var ra = new World(1, 7, 64) { Open = true, MaxPlayers = 8, MaxMapSize = 320 };
            var first = ra.AddTeam("llm", "One", "Ghost Crew", out err, out problem);
            while (ra.Teams.Count < World.Flavors.Length) ra.AddTeam("llm", "P" + ra.Teams.Count, null, out err, out problem);
            ra.Leave(first.Id);
            Check(ra.Teams[first.Id].Name == "Ghost Crew" && ra.Teams[first.Id].Left, "a departed team keeps its name in the record until the seat is reused");
            var heir = ra.AddTeam("llm", "Heir", null, out err, out problem);
            Check(heir != null && heir.Id == first.Id && heir.Name == World.Flavors[heir.Id] && heir.Seat != first.Seat, $"the next occupant of the seat gets the flavour, not the old name ({heir?.Name})");
            ra.Leave(heir.Id);
            var heir2 = ra.AddTeam("llm", "Heir2", "Ghost Crew", out err, out problem);
            Check(heir2 != null && heir2.Id == first.Id && heir2.Name == "Ghost Crew" && problem == null, "or picks their own, which may be a name the seat's last occupant used");

            TeamNamesOverHttp();
        }

        /// <summary>The wire: /api/register and /api/join take team_name and answer with the name the team ended up with.</summary>
        static void TeamNamesOverHttp()
        {
            var game = new Game(new GameConfig { Seed = 3, MapSize = 64, Open = true, Controllers = new[] { "ai" }, HouseAIs = 1 });
            var api = new ApiServer(7989);
            api.Start();
            bool halt = false;
            var loop = new Thread(() =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew(); double last = 0;
                while (!halt) { double now = clock.Elapsed.TotalSeconds; game.Advance((float)(now - last)); last = now; api.Pump(game); Thread.Sleep(5); }
            }) { IsBackground = true };
            loop.Start();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            Dictionary<string, object> Post(string path, string body) => (Dictionary<string, object>)Json.Parse(http.PostAsync($"http://127.0.0.1:7989{path}", new StringContent(body)).Result.Content.ReadAsStringAsync().Result);
            Dictionary<string, object> Get(string path) => (Dictionary<string, object>)Json.Parse(http.GetStringAsync($"http://127.0.0.1:7989{path}").Result);

            var r1 = Post("/api/register", "{\"name\":\"Claude\",\"team_name\":\"Hive Mind\"}");
            Check(r1.Str("team_name") == "Hive Mind" && r1.Str("flavor") == "Hive Mind" && !r1.ContainsKey("team_name_rejected"), $"register answers with the effective team_name ({r1.Str("team_name")})");
            var r2 = Post("/api/register", "{\"name\":\"Codex\",\"team_name\":\"hive mind\"}");
            Check(r2.Str("team_name") == r2.Str("flavor") && r2.Str("team_name") != "hive mind" && r2.Str("team_name_rejected") != null, $"a duplicate keeps the flavour and says why ({r2.Str("team_name")}: {r2.Str("team_name_rejected")})");
            var r3 = Post("/api/register", "{\"name\":\"Gemini\"}");
            Check(r3.Str("team_name") == r3.Str("flavor") && !r3.ContainsKey("team_name_rejected"), $"no team_name: the flavour, nothing rejected ({r3.Str("team_name")})");
            var lobby = Get("/api/lobby");
            var names = ((List<object>)lobby["teams"]).Select(t => ((Dictionary<string, object>)t).Str("flavor")).ToList();
            Check(names.Contains("Hive Mind"), $"the lobby shows the chosen name ({string.Join(", ", names)})");
            var status = Json.Write(Get("/api/status"));
            Check(status.Contains("Hive Mind"), "so does /api/status");

            // Taking over a fixed seat (the local MCP server): the name is optional there too.
            var j1 = Post("/api/join?team=0", "{\"name\":\"Bot\",\"team_name\":\"Anvil\"}");
            Check(j1.Str("team_name") == "Anvil" && j1.Str("name") == "Anvil", $"/api/join names the seat ({j1.Str("team_name")})");
            var j2 = Post("/api/join?team=0", "{\"name\":\"Bot\"}");
            Check(j2.Str("team_name") == "Anvil", "joining again without a name leaves it alone");
            var j3 = Post("/api/join?team=0", "{\"name\":\"Bot\",\"team_name\":\"hive MIND\"}");
            Check(j3.Str("team_name") == "Anvil" && j3.Str("team_name_rejected") != null, $"and a taken name is refused there too ({j3.Str("team_name_rejected")})");

            halt = true; loop.Join(); api.Stop();
        }
    }
}
