using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public enum MatchPhase { Unlimited, Normal, SuddenDeath, Decay, Ended }

    /// <summary>One team's standing on the match's scoreboard (World.Scores).</summary>
    public class MatchScore
    {
        public int Team;
        public string Name, Player;
        /// <summary>OreMined is what the economy points count: ore from fields and deep mines, not salvage picked up.</summary>
        public int Territory, OreMined, KillValue;
        public float TerritoryPoints, EconomyPoints, KillPoints;
        public int Score;
    }

    /// <summary>
    /// The match clock. Open arenas used to run forever and stall; now every game has a length (GameConfig.MatchHours,
    /// default 4 h of game time):
    ///  - at SuddenDeathAt sudden death begins: ore stops regrowing;
    ///  - DecayAfter (30 min) later structures slowly decay (repair trucks keep them up);
    ///  - EndAfter (60 min) after sudden death the match ends on points (Scores): each team's share of the territory
    ///    held, of the ore mined and of the value destroyed, a third each.
    /// Each stage is announced 10 minutes and 1 minute ahead in the arena chat and as a MATCH CLOCK alert, and the state's
    /// `match` says where the clock is. An open arena shows the results for ResultsSeconds, then starts a new match
    /// (Game.Advance).
    /// </summary>
    public partial class World
    {
        /// <summary>Game time (s) when sudden death begins; 0 = no clock (a world built without a Game, as in tests).</summary>
        public float SuddenDeathAt;
        /// <summary>After a match ends in an open arena: seconds until the next one starts (-1 = none coming). Mirrors Game's countdown for the state.</summary>
        public float NextMatchIn = -1;
        public const float DecayAfter = 30 * 60f, EndAfter = 60 * 60f;
        /// <summary>A resumed game that's already past (or nearly at) sudden death gets this long before it starts.</summary>
        public const float LateClockGrace = 30 * 60f;
        /// <summary>Health a structure loses a second in the decay stage: 0.03% of its maximum (about half over the half hour).</summary>
        public const float DecayPerSecond = 0.0003f;
        /// <summary>Territory: the ground within this many tiles of a team's finished structures (the build-reach rule).</summary>
        public const int TerritoryReach = 6;
        /// <summary>
        /// Every other structure claims only this much. Territory is ground you hold economically: command centers,
        /// refineries and outposts (drop-offs), derricks and deep mines claim TerritoryReach around them. Chains of cheap
        /// turrets, then of cheap barracks, across the map (test players Hazel, Iris, Juniper) were the cheapest points on
        /// the board and pulled players away from fighting and mining.
        /// </summary>
        public const int SmallTerritoryReach = 2;
        public static bool Holds(EntityDef d) => d.DropOff || d.Key == "derrick" || d.Key == "deep_mine";
        /// <summary>A refinery holds ground only where there's ore to hold (a surface field within RefineryFieldReach); a
        /// chain of refineries across empty ground (test player Kestrel) is just buildings.</summary>
        public const int RefineryFieldReach = 10;
        public int ReachOf(Entity e)
        {
            if (!Holds(e.Def)) return SmallTerritoryReach;
            if (e.Def.Key != "mining_refinery") return TerritoryReach;
            var c = Int2.Of(e.Center);
            for (int y = Math.Max(0, c.Y - RefineryFieldReach); y <= Math.Min(Map.H - 1, c.Y + RefineryFieldReach); y++)
                for (int x = Math.Max(0, c.X - RefineryFieldReach); x <= Math.Min(Map.W - 1, c.X + RefineryFieldReach); x++)
                    if (Map.OreBase[Map.Idx(x, y)] > 0) return TerritoryReach;
            return SmallTerritoryReach;
        }

        public float DecayAt => SuddenDeathAt + DecayAfter;
        public float MatchEndsAt => SuddenDeathAt + EndAfter;

        public MatchPhase Phase =>
            SuddenDeathAt <= 0 ? MatchPhase.Unlimited
            : Time >= MatchEndsAt ? MatchPhase.Ended
            : Time >= DecayAt ? MatchPhase.Decay
            : Time >= SuddenDeathAt ? MatchPhase.SuddenDeath
            : MatchPhase.Normal;

        /// <summary>This structure is decaying (the decay stage; or the kitchen sink's exhibit of it): the view crumbles it.</summary>
        public bool Decaying(Entity e) =>
            e.IsStructure && e.IsComplete && e.Team >= 0 &&
            ((Phase == MatchPhase.Decay && !IsProtected(e.Team)) || (Showcase != null && Showcase.Decaying.Contains(e.Id)));

        /// <summary>The game ended because the clock ran out (not by elimination).</summary>
        public bool MatchOver => GameOver && SuddenDeathAt > 0 && Time >= MatchEndsAt;

        public static string PhaseName(MatchPhase p) => p switch
        {
            MatchPhase.Unlimited => "unlimited",
            MatchPhase.Normal => "normal",
            MatchPhase.SuddenDeath => "sudden_death",
            MatchPhase.Decay => "decay",
            _ => "ended",
        };

        /// <summary>The next stage and when it starts (null when there's none).</summary>
        public (string phase, float at)? NextStage() => Phase switch
        {
            MatchPhase.Normal => ("sudden_death", SuddenDeathAt),
            MatchPhase.SuddenDeath => ("decay", DecayAt),
            MatchPhase.Decay => ("ended", MatchEndsAt),
            _ => ((string, float)?)null,
        };

        static string Clock(float seconds)
        {
            int s = (int)MathF.Ceiling(MathF.Max(0, seconds));
            return s >= 3600 ? $"{s / 3600}h{s % 3600 / 60:00}m" : $"{s / 60}:{s % 60:00}";
        }

        // ------------------------------------------------------------------ the scoreboard

        List<MatchScore> scoreCache;
        int scoreCacheTick = -1;

        /// <summary>
        /// The scoreboard: every team still playing, best first. Points are shares of what all of them hold between them,
        /// a third each (100 points per component): territory (tiles within TerritoryReach of its finished structures),
        /// economy (ore mined, all game) and kills (the ore value of everything it destroyed). Recomputed at most once a second.
        /// </summary>
        /// <summary>A mirror's scores as the server counted them (ApiServer.ViewSnapshot's "mirror_scores"), or null.</summary>
        public List<MatchScore> MirrorScores;

        public List<MatchScore> Scores()
        {
            if (Mirror && MirrorScores != null) return MirrorScores; // a fogged copy can't count rivals' territory: the server's own count
            if (scoreCache != null && Tick / TickRate == scoreCacheTick) return scoreCache;
            var playing = Teams.Where(t => !t.Left && !t.Defeated).ToList();
            var list = new List<MatchScore>();
            var claimed = new bool[Map.W * Map.H];
            foreach (var t in playing)
            {
                Array.Clear(claimed, 0, claimed.Length);
                int tiles = 0;
                foreach (var e in Entities)
                {
                    if (e.Dead || e.Team != t.Id || !e.IsStructure || !e.IsComplete) continue;
                    int reach = ReachOf(e);
                    for (int y = e.Origin.Y - reach; y < e.Origin.Y + e.Def.SizeY + reach; y++)
                        for (int x = e.Origin.X - reach; x < e.Origin.X + e.Def.SizeX + reach; x++)
                        {
                            if (!Map.InBounds(x, y)) continue;
                            int i = Map.Idx(x, y);
                            if (!claimed[i]) { claimed[i] = true; tiles++; }
                        }
                }
                list.Add(new MatchScore { Team = t.Id, Name = t.Name, Player = t.PlayerName ?? t.Controller, Territory = tiles, OreMined = Math.Max(0, t.Stats.OreMined - t.Stats.SalvageMined), KillValue = t.Stats.KillValue });
            }
            float tt = list.Sum(s => (float)s.Territory), te = list.Sum(s => (float)s.OreMined), tk = list.Sum(s => (float)s.KillValue);
            foreach (var s in list)
            {
                s.TerritoryPoints = tt > 0 ? 100f * s.Territory / tt : 0;
                s.EconomyPoints = te > 0 ? 100f * s.OreMined / te : 0;
                s.KillPoints = tk > 0 ? 100f * s.KillValue / tk : 0;
                s.Score = (int)MathF.Round(s.TerritoryPoints + s.EconomyPoints + s.KillPoints);
            }
            scoreCache = list.OrderByDescending(s => s.Score).ThenBy(s => s.Team).ToList();
            scoreCacheTick = Tick / TickRate;
            return scoreCache;
        }

        public const string HowScored = "each team's share of what all players still in hold between them, 100 points each: territory (tiles within 6 of your finished command centers, outposts, derricks, deep mines and refineries by an ore field; 2 around any other structure), economy (ore mined from fields and deep mines all game; salvage you pick up doesn't count) and kills (the ore value of everything you destroyed: a derrick is about 600); the most points wins";

        public string ScoreLine() => string.Join(", ", Scores().Select(s => $"{s.Name} {s.Score}"));

        // ------------------------------------------------------------------ the clock

        bool Crossed(float at) => at > 0 && (Tick - 1) * Dt < at && Time >= at;

        internal void Announce(string text)
        {
            Emit("chat", -1, text: text);
            Emit("match", -1, text: text);
            foreach (var t in Teams.Where(t => !t.Left && !t.Defeated))
                Alerts.Raise(this, t.Id, "match_clock", Priority.High, t.StartPos, hit: false).Lost.Add(text);
        }

        void MatchClock()
        {
            if (SuddenDeathAt <= 0) return;
            foreach (float ahead in new[] { 600f, 60f })
            {
                string inT = ahead >= 600 ? "10 minutes" : "1 minute";
                if (Crossed(SuddenDeathAt - ahead))
                    Announce($"Sudden death in {inT}: ore stops regrowing. Structures start to decay 30 minutes after that, and the match ends on points 60 minutes after it. Scores now: {ScoreLine()}.");
                if (Crossed(DecayAt - ahead))
                    Announce($"Structures start to decay in {inT} (0.03% of their health a second; repair trucks keep them up, and anything below 50% can be captured by an engineer). The match ends on points 30 minutes after that. Scores now: {ScoreLine()}.");
                if (Crossed(MatchEndsAt - ahead))
                    Announce($"The match ends on points in {inT}. Scores now: {ScoreLine()}. Points: {HowScored}.");
            }
            if (Crossed(SuddenDeathAt))
                Announce($"SUDDEN DEATH: ore no longer regrows. Structures start to decay in 30 minutes; the match ends on points in 60 minutes. Scores now: {ScoreLine()}.");
            if (Crossed(DecayAt))
                Announce($"DECAY: every structure now loses 0.03% of its health a second (repair trucks keep it up; below 50% an engineer can take it). The match ends on points in 30 minutes. Scores now: {ScoreLine()}.");
            if (Phase == MatchPhase.Decay && Tick % TickRate == 0)
                foreach (var e in Entities.ToList())
                    if (!e.Dead && e.IsStructure && e.IsComplete && e.Team >= 0 && !IsProtected(e.Team))
                        Damage(e, e.Def.MaxHp * DecayPerSecond, null, -1, burn: true); // no alerts: it's the clock, not an attack
            if (Time >= MatchEndsAt && !GameOver) EndByScore();
        }

        void EndByScore()
        {
            var s = Scores();
            scoreCache = null; // recount at the whistle
            s = Scores();
            GameOver = true;
            Winner = s.Count == 0 ? -1 : s.Count == 1 || s[0].Score > s[1].Score ? s[0].Team : -1;
            string board = string.Join(", ", s.Select(x => $"{x.Name} ({x.Player}) {x.Score}"));
            string text = Winner >= 0 ? $"Time! {Teams[Winner].Name} ({Teams[Winner].PlayerName ?? Teams[Winner].Controller}) wins the match on points: {board}."
                                      : $"Time! The match ends in a draw on points: {board}.";
            Emit("game_over", Winner, text: text);
            if (Open) Emit("chat", -1, text: $"A new match starts in {Game.ResultsSeconds / 60:0} minutes, on a new map: join again then.");
        }
    }

    public partial class Game
    {
        /// <summary>Open arena: how long the results stay up after a match ends on points before a new match starts.</summary>
        public const float ResultsSeconds = 180f;
        /// <summary>Seconds of game time until the next match (counting only after a match has ended; -1 = not counting).</summary>
        float restartIn = -1;
        public float RestartIn => restartIn;
        /// <summary>Hosts: a new match replaced the finished one (the saved game is forgotten, as for an admin restart).</summary>
        public Action Restarted;

        /// <summary>The match is over in an open arena: count down, then start the next one on a new map.</summary>
        void ResultsCountdown(float realDt)
        {
            var w = World;
            if (!Config.Open || Config.KitchenSink || !w.MatchOver || w.Mirror) return;
            if (restartIn < 0) restartIn = ResultsSeconds;
            float before = restartIn;
            restartIn -= realDt * Speed;
            w.NextMatchIn = MathF.Max(0, restartIn);
            foreach (float mark in new[] { 60f, 10f })
                if (before > mark && restartIn <= mark) w.Emit("chat", -1, text: $"A new match starts in {mark:0} seconds.");
            if (restartIn > 0) return;
            NextMatch();
        }

        /// <summary>
        /// Start the next match: same room settings, a new map (the seed moves on). Only the house AIs get seats: outside
        /// players join again, and a seat saved for one ("llm") would sit idle and keep scoring.
        /// </summary>
        public void NextMatch()
        {
            var c = Config;
            var cfg = new GameConfig
            {
                Seed = unchecked(c.Seed * 31 + 17), MapSize = c.MapSize, Speed = c.Speed, Controllers = Enumerable.Repeat("ai", c.HouseAIs).ToArray(),
                Orders = null, Open = c.Open, MaxPlayers = c.MaxPlayers, MaxMapSize = c.MaxMapSize, OreScale = c.OreScale,
                HouseAIs = c.HouseAIs, HouseResignAbove = c.HouseResignAbove, MatchHours = c.MatchHours, MinPlayers = c.MinPlayers,
            };
            Restart(cfg);
            World.Emit("chat", -1, text: Waiting ? $"The next match is open: it starts when {cfg.MinPlayers} players are ready, or 120 seconds after they are seated. Join again for a seat."
                                                 : $"A new match has begun ({(cfg.MatchHours > 0 ? $"{cfg.MatchHours:0.#} h until sudden death" : "no time limit")}). Join again for a seat.");
            Restarted?.Invoke();
        }

        // ------------------------------------------------------------------ waiting rooms

        /// <summary>
        /// A room that waits for players (GameConfig.MinPlayers): frozen (no ticks, no game time) until that many outside
        /// agents are ready (or 120 real seconds after enough seats fill), so everyone starts together at t=0. Separate
        /// from the operator's Paused. Commands other than ready, say and start_match are refused (Execute).
        /// </summary>
        public bool Waiting;
        /// <summary>Real seconds a lone agent waits before it may start against the scripted AI instead (start_match).</summary>
        public const float ChoiceAfter = 300f;
        public const float ReadyGraceSeconds = 120f;
        /// <summary>The wall clock for the real time waited (tests set their own).</summary>
        public Func<DateTime> Now = () => DateTime.UtcNow;
        /// <summary>Real seconds waited before waitingSince (a resumed room keeps how long it's waited); waitedSeen: as of the last frame.</summary>
        float waitedBefore, waitedSeen;
        DateTime waitingSince;
        // Like the room's waiting clock, save elapsed time and resume it on the new host's wall clock.
        float readyFilledBefore = -1f;
        DateTime readyFilledSince;

        public float WaitedSeconds => Waiting ? waitedBefore + (float)(Now() - waitingSince).TotalSeconds : waitedBefore;
        /// <summary>Outside agents seated (not house AI, not gone): what MinPlayers counts.</summary>
        public int OutsidePlayers => World.Teams.Count(IsOutsideSeat);
        public int OutsideReadyPlayers => World.Teams.Count(t => IsOutsideSeat(t) && t.Ready);
        static bool IsOutsideSeat(Team t) => t.Controller == "llm" && !t.House && !t.Left && !t.Defeated;
        float ReadyFilledSeconds => Waiting && OutsidePlayers >= Config.MinPlayers && readyFilledBefore >= 0
            ? readyFilledBefore + MathF.Max(0, (float)(Now() - readyFilledSince).TotalSeconds) : -1f;
        /// <summary>-1 until enough seats fill; otherwise real seconds left before an unready match starts.</summary>
        public float ReadyCountdownSeconds => ReadyFilledSeconds < 0 ? -1f : MathF.Max(0, ReadyGraceSeconds - ReadyFilledSeconds);

        void CheckReadyStart()
        {
            if (!Waiting) return;
            if (OutsidePlayers < Config.MinPlayers) { readyFilledBefore = -1f; return; }
            if (readyFilledBefore < 0) { readyFilledBefore = 0; readyFilledSince = Now(); }
            if (OutsideReadyPlayers >= Config.MinPlayers) BeginMatch($"{OutsideReadyPlayers} players are ready");
            else if (ReadyFilledSeconds >= ReadyGraceSeconds) BeginMatch($"{OutsidePlayers} players are seated and the 120-second preparation period has ended");
        }

        /// <summary>Every frame while waiting: once a lone agent has waited ChoiceAfter, an alert says what it can do.</summary>
        void WaitingClock()
        {
            CheckReadyStart();
            if (!Waiting) return;
            float waited = WaitedSeconds;
            if (waitedSeen < ChoiceAfter && waited >= ChoiceAfter && OutsidePlayers == 1)
                foreach (var t in World.Teams.Where(IsOutsideSeat))
                    World.Alerts.Raise(World, t.Id, "waiting_choice", Priority.High, t.StartPos, hit: false).Lost.Add(WaitingText());
            waitedSeen = waited;
        }

        /// <summary>A player takes a seat (ApiServer's register). In a waiting room it's a founder; enough seats start a preparation countdown.</summary>
        public Team Join(string controller, string playerName, string teamName, out string error, out string nameProblem)
        {
            // A seat may have left since the last frame: clear its preparation clock before filling it again.
            CheckReadyStart();
            var t = World.AddTeam(controller, playerName, teamName, out error, out nameProblem);
            if (t == null || !Waiting) return t;
            t.ProtectedUntil = 0; // founders start together: no newcomer protection (and at game time 0, no catch-up kit)
            CheckReadyStart();
            return t;
        }

        void BeginMatch(string how)
        {
            waitedBefore = WaitedSeconds;
            Waiting = false;
            World.Announce($"The match has started: {how}. The clock runs from now ({(Config.MatchHours > 0 ? $"{Config.MatchHours:0.#} h until sudden death" : "no time limit")}). Everyone starts together, with no newcomer protection.");
        }

        /// <summary>One command from a player over the API: start_match and the waiting room's refusals, then Commands.Execute.</summary>
        public JObj Execute(int team, Dictionary<string, object> c)
        {
            var type = c.Str("type", "").ToLowerInvariant();
            if (type == "ready") return SetReady(team);
            if (type == "start_match") return StartMatch(team, c.Str("opponent"));
            if (Waiting && type != "say") return Fail($"waiting_for_players: {WaitingText()} Until then only ready, say and start_match work.");
            return Commands.Execute(World, team, c);
        }

        static JObj Fail(string error) => new JObj().Set("ok", false).Set("error", error);

        JObj SetReady(int team)
        {
            if (team < 0 || team >= World.Teams.Count || !IsOutsideSeat(World.Teams[team]))
                return Fail("ready needs your own live outside seat");
            World.Teams[team].Ready = true;
            CheckReadyStart();
            var reply = new JObj().Set("ok", true).Set("ready", true).Set("ready_players", OutsideReadyPlayers).Set("room_status", RoomStatus)
                .Set("result", Waiting ? "ready; the room is still waiting" : "ready; the match is running");
            if (Waiting) reply.Set("waiting", WaitingJson());
            return reply;
        }

        /// <summary>A lone agent who has waited ChoiceAfter starts now against the scripted AI: the aggressive one (attack waves), seated as house.</summary>
        JObj StartMatch(int team, string opponent)
        {
            if (!Waiting) return Fail("start_match: the match is already running");
            if (opponent != "scripted") return Fail("start_match needs \"opponent\":\"scripted\" (the scripted AI is the only sparring partner)");
            int seated = OutsidePlayers;
            if (seated != 1) return Fail($"start_match is for a player waiting alone; {seated} players are seated, and the match starts when {Config.MinPlayers} are ready or 120 seconds after enough seats fill");
            float left = ChoiceAfter - WaitedSeconds;
            if (left > 0) return Fail($"start_match opens after {ChoiceAfter / 60:0} minutes of waiting: {StateView.Secs(left)}s to go. Until then the room waits for players");
            var ai = World.AddTeam("ai", "Scripted AI", out var err);
            if (ai == null) return Fail("start_match: " + err);
            ai.House = true; // not an outside player: it doesn't count toward min_players, or keep the room open
            ai.ProtectedUntil = 0;
            ais.RemoveAll(a => a.Team == ai.Id); // a recycled seat may still have the old occupant's AI
            ais.Add(new SimpleAI(ai.Id, passive: false));
            BeginMatch($"{World.Teams[team].Name} against the scripted AI ({ai.Name})");
            return new JObj().Set("ok", true).Set("result", $"the match has started against the scripted AI ({ai.Name}: it attacks, in waves of 6 to 10)");
        }

        /// <summary>What a waiting room is waiting for, in a sentence, with the lone agent's choice once it's open.</summary>
        public string WaitingText()
        {
            int seated = OutsidePlayers, min = Config.MinPlayers;
            float waited = WaitedSeconds;
            var s = $"waiting for players: {seated} of {min} seated, {OutsideReadyPlayers} ready, {StateView.Hms(waited)} waited. Send {{\"type\":\"ready\"}} when prepared. The match starts when {min} are ready, or 120 seconds after enough seats fill; no game time passes until then.";
            if (ReadyCountdownSeconds >= 0) s += $" Automatic start in {StateView.Secs(ReadyCountdownSeconds)}s.";
            if (seated != 1) return s;
            return waited < ChoiceAfter ? s + $" If you're still alone in {StateView.Hms(ChoiceAfter - waited)}, you can start against the scripted AI instead."
                : s + " You've waited 5 minutes alone, so choose: keep waiting (do nothing); leave, then join a running room by its code (the lobby lists them); " +
                      "or start now against the scripted AI: {\"type\":\"start_match\",\"opponent\":\"scripted\"}.";
        }

        public JObj WaitingJson() => new JObj().Set("players", OutsidePlayers).Set("min_players", Config.MinPlayers)
            .Set("ready_players", OutsideReadyPlayers).Set("ready_seats", World.Teams.Where(IsOutsideSeat).Select(t => (object)new JObj()
                .Set("team", t.Id).Set("team_name", t.Name).Set("player", t.PlayerName).Set("ready", t.Ready)).ToList())
            .Set("start_after_s", ReadyCountdownSeconds < 0 ? -1 : StateView.Secs(ReadyCountdownSeconds))
            .Set("waited_s", StateView.Secs(WaitedSeconds)).Set("choose_after_s", StateView.Secs(ChoiceAfter - WaitedSeconds)).Set("text", WaitingText());

        // ------------------------------------------------------------------ the lobby

        /// <summary>waiting (gathering players, frozen), playing, closing (sudden death or later: no joins) or results (the match is over).</summary>
        public string RoomStatus => Waiting ? "waiting" : World.GameOver ? "results" : World.Phase >= MatchPhase.SuddenDeath ? "closing" : "playing";

        /// <summary>Game seconds until the next match starts (the results countdown, or the clock plus ResultsSeconds); null if none is coming.</summary>
        public float? NextMatchInS =>
            !Config.Open || Config.KitchenSink || World.SuddenDeathAt <= 0 || (World.GameOver && !World.MatchOver) ? (float?)null
            : World.GameOver ? (restartIn >= 0 ? restartIn : ResultsSeconds)
            : World.MatchEndsAt - World.Time + ResultsSeconds;

        /// <summary>Why the room takes no new players now (null: it does): sudden death or later is closed to every join, even by code, as a newcomer can't catch up.</summary>
        public string ClosedToJoins()
        {
            var next = NextMatchInS;
            string when = next.HasValue ? $"; its next match starts in {StateView.Hms(next.Value)}" : "";
            if (World.GameOver) return $"this match is over and its results are showing{when}. Join a room that's playing or waiting (the lobby lists them)";
            if (World.Phase >= MatchPhase.SuddenDeath) return $"this match is in sudden death and closed to new players, who couldn't catch up{when}. Join a room that's playing or waiting (the lobby lists them)";
            return null;
        }

        /// <summary>The lobby's why for a room that can't be joined; null when it can.</summary>
        public string WhyNotJoinable()
        {
            var w = World; var next = NextMatchInS;
            if (!w.Open) return "not open for joining";
            if (w.GameOver) return "match over: results showing" + (next.HasValue ? $"; next match in {StateView.Secs(next.Value)}s" : "");
            if (w.Phase >= MatchPhase.SuddenDeath) return "sudden death: closed to new players";
            if (w.SeatsFree == 0) return $"all {Math.Min(w.MaxPlayers, World.Flavors.Length)} seats taken";
            return null;
        }

        public const float FreshFor = 15 * 60f, LateWithin = 30 * 60f;

        /// <summary>
        /// The lobby's read on a room: fresh (waiting, or the first FreshFor of game time), early (before a third of the time to
        /// sudden death), midgame, then late (less than LateWithin to sudden death, closing or results; late wins over fresh in
        /// a short match). A room with no clock is fresh, then midgame.
        /// </summary>
        public string Verdict()
        {
            var w = World;
            if (Waiting) return "fresh";
            if (RoomStatus != "playing" || (w.SuddenDeathAt > 0 && w.SuddenDeathAt - w.Time < LateWithin)) return "late";
            if (w.Time < FreshFor) return "fresh";
            return w.SuddenDeathAt > 0 && w.Time < w.SuddenDeathAt / 3 ? "early" : "midgame";
        }

        /// <summary>The verdict in one plain line: time to sudden death, who's active, the leader's share of the points.</summary>
        public string VerdictText()
        {
            var w = World;
            if (Waiting) return WaitingText();
            if (w.GameOver) return $"match over: {(w.Winner >= 0 ? $"{w.Teams[w.Winner].Name} won" : "no winner")}; " + (NextMatchInS is float n ? $"next match in {StateView.Hms(n)}." : "no next match.");
            var scores = w.Scores();
            int total = scores.Sum(s => s.Score);
            string clock = w.SuddenDeathAt <= 0 ? "no time limit"
                : w.Phase >= MatchPhase.SuddenDeath ? $"sudden death: the match ends on points in {StateView.Hms(w.MatchEndsAt - w.Time)}"
                : $"{StateView.Hms(w.SuddenDeathAt - w.Time)} to sudden death";
            int active = w.Teams.Count(t => !t.House && w.IsActive(t));
            return $"{clock}; {active} active player{(active == 1 ? "" : "s")}; " +
                   (total > 0 ? $"the leader ({scores[0].Name}) has {100 * scores[0].Score / total}% of the points." : "no points scored yet.");
        }
    }
}
