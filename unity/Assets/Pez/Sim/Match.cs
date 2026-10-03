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
        public List<MatchScore> Scores()
        {
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
                    for (int y = e.Origin.Y - TerritoryReach; y < e.Origin.Y + e.Def.SizeY + TerritoryReach; y++)
                        for (int x = e.Origin.X - TerritoryReach; x < e.Origin.X + e.Def.SizeX + TerritoryReach; x++)
                        {
                            if (!Map.InBounds(x, y)) continue;
                            int i = Map.Idx(x, y);
                            if (!claimed[i]) { claimed[i] = true; tiles++; }
                        }
                }
                list.Add(new MatchScore { Team = t.Id, Name = t.Name, Player = t.PlayerName ?? t.Controller, Territory = tiles, OreMined = t.Stats.OreMined, KillValue = t.Stats.KillValue });
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

        public const string HowScored = "each team's share of what all players still in hold between them, 100 points each: territory (tiles within 6 of your finished structures), economy (ore mined all game) and kills (the ore value of everything you destroyed); the most points wins";

        public string ScoreLine() => string.Join(", ", Scores().Select(s => $"{s.Name} {s.Score}"));

        // ------------------------------------------------------------------ the clock

        bool Crossed(float at) => at > 0 && (Tick - 1) * Dt < at && Time >= at;

        void Announce(string text)
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
            if (!Config.Open || Config.KitchenSink || !w.MatchOver) return;
            if (restartIn < 0) restartIn = ResultsSeconds;
            float before = restartIn;
            restartIn -= realDt * Speed;
            w.NextMatchIn = MathF.Max(0, restartIn);
            foreach (float mark in new[] { 60f, 10f })
                if (before > mark && restartIn <= mark) w.Emit("chat", -1, text: $"A new match starts in {mark:0} seconds.");
            if (restartIn > 0) return;
            NextMatch();
        }

        /// <summary>Start the next match: same room settings, a new map (the seed moves on).</summary>
        public void NextMatch()
        {
            var c = Config;
            var cfg = new GameConfig
            {
                Seed = unchecked(c.Seed * 31 + 17), MapSize = c.MapSize, Speed = c.Speed, Controllers = (string[])c.Controllers.Clone(),
                Orders = (string[])c.Orders?.Clone(), Open = c.Open, MaxPlayers = c.MaxPlayers, MaxMapSize = c.MaxMapSize, OreScale = c.OreScale,
                HouseAIs = c.HouseAIs, HouseResignAbove = c.HouseResignAbove, MatchHours = c.MatchHours,
            };
            Restart(cfg);
            World.Emit("chat", -1, text: $"A new match has begun ({(cfg.MatchHours > 0 ? $"{cfg.MatchHours:0.#} h until sudden death" : "no time limit")}). Join again for a seat.");
            Restarted?.Invoke();
        }
    }
}
