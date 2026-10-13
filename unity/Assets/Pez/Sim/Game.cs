using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public class GameConfig
    {
        public int Seed = 1337;
        public int MapSize = 80;
        public float Speed = 1f;
        /// <summary>Per team: human | ai | llm</summary>
        public string[] Controllers = { "human", "ai" };
        /// <summary>Per team: initial standing orders for an LLM commander (may be null/empty).</summary>
        public string[] Orders = { "", "" };
        /// <summary>Open arena: outside agents can join and leave mid-game; the map grows with each join.</summary>
        public bool Open;
        public int MaxPlayers = 8;
        public int MaxMapSize = Map.MaxSize;
        /// <summary>Surface ore richness (1 normal; 0.3 = scarce, deep mining matters early).</summary>
        public float OreScale = 1f;
        /// <summary>Open arena: scripted house players kept in the world while no outside players are left.</summary>
        public int HouseAIs = 1;
        /// <summary>Open arena: house players resign (leaving salvage) once more than this many players are active.</summary>
        public int HouseResignAbove = 5;
        /// <summary>The kitchen sink: a showcase room holding every asset in every state (KitchenSink.cs), not a game.</summary>
        public bool KitchenSink;
        /// <summary>Game hours until sudden death (the match clock, Match.cs); the match ends on points an hour after. 0 = no clock.</summary>
        public float MatchHours = 4f;
        /// <summary>Open arena: each match waits, frozen, until this many outside agents are seated (Game.Waiting). 0 = it starts at once.</summary>
        public int MinPlayers;
    }

    /// <summary>Owns a World plus who controls each team, and advances it in real time.</summary>
    public partial class Game
    {
        public World World { get; private set; }
        public GameConfig Config { get; private set; }
        public float Speed;
        public bool Paused;
        /// <summary>Frames per second the host is drawing at (0 on headless servers), for monitoring.</summary>
        public float RenderFps;
        readonly List<SimpleAI> ais = new List<SimpleAI>();
        float accumulator, nextHouseCheck;

        public Game(GameConfig cfg) { Restart(cfg); }

        public void Restart(GameConfig cfg)
        {
            Config = cfg;
            Speed = cfg.Speed;
            // A waiting room starts empty: no house AI, only the agents who join (and, if they choose, a sparring AI).
            Waiting = cfg.MinPlayers > 0;
            waitedBefore = waitedSeen = 0;
            waitingSince = Now();
            World = cfg.KitchenSink ? Sim.KitchenSink.Build()
                  : new World(Waiting ? 0 : cfg.Controllers.Length, cfg.Seed, cfg.MapSize, cfg.OreScale) { Open = cfg.Open, MaxPlayers = cfg.MaxPlayers, MaxMapSize = cfg.MaxMapSize };
            ais.Clear();
            for (int i = 0; i < cfg.Controllers.Length && i < World.Teams.Count; i++)
            {
                World.Teams[i].Controller = cfg.Controllers[i];
                if (cfg.Controllers[i] == "ai")
                {
                    // In an open arena, scripted seats are passive house players.
                    ais.Add(new SimpleAI(i, passive: cfg.Open));
                    World.Teams[i].PlayerName = cfg.Open ? "House AI" : "Scripted AI";
                    World.Teams[i].House = cfg.Open;
                }
                if (cfg.Orders != null && i < cfg.Orders.Length && !string.IsNullOrWhiteSpace(cfg.Orders[i])) World.SetOrders(i, cfg.Orders[i]);
            }
            if (!cfg.KitchenSink && cfg.MatchHours > 0) World.SuddenDeathAt = cfg.MatchHours * 3600f;
            accumulator = 0;
            nextHouseCheck = 0;
            restartIn = -1;
        }

        /// <summary>
        /// Open arena house policy: house players make room when the arena gets busy (more than HouseResignAbove
        /// active players: they resign and their base becomes salvage), and come back when every outside player has gone.
        /// </summary>
        void HousePolicy()
        {
            var w = World;
            if (!Config.Open || w.Time < nextHouseCheck) return;
            nextHouseCheck = w.Time + 1f;
            var houses = w.Teams.Where(t => t.House && !t.Left && !t.Defeated).ToList();
            int outsiders = w.Teams.Count(t => !t.House && !t.Left && !t.Defeated);
            if (w.ActivePlayers > Config.HouseResignAbove && houses.Count > 0)
            {
                var h = houses[0];
                w.Emit("chat", -1, text: $"The house AI ({h.Name}) resigns to make room. Its base is now salvage.");
                w.Leave(h.Id);
                ais.RemoveAll(a => a.Team == h.Id);
                return;
            }
            if (outsiders == 0 && houses.Count < Config.HouseAIs)
            {
                var t = w.AddTeam("ai", "House AI", out _);
                if (t == null) return;
                t.House = true;
                ais.RemoveAll(a => a.Team == t.Id); // a recycled seat may still have the old occupant's AI
                ais.Add(new SimpleAI(t.Id, passive: true));
            }
            // Retire controllers for house seats that are gone.
            ais.RemoveAll(a => w.Teams[a.Team].Left || (w.Teams[a.Team].Defeated && w.Teams[a.Team].House));
        }

        /// <summary>Advance by real elapsed seconds. Returns ticks stepped.</summary>
        public int Advance(float realDt)
        {
            if (Paused) return 0;
            if (Waiting) { WaitingClock(); return 0; }
            if (World.GameOver) { ResultsCountdown(realDt); return 0; }
            accumulator += realDt * Speed;
            int steps = 0;
            // Generous catch-up so a throttled frame rate (window hidden, App Nap) doesn't slow the game clock.
            while (accumulator >= World.Dt && steps < 100)
            {
                accumulator -= World.Dt;
                Step();
                steps++;
            }
            if (steps == 100) accumulator = 0; // don't spiral if we fall far behind
            return steps;
        }

        /// <summary>
        /// A view's copy (WebClient): orders players gave the host (World.LogCommand), by number, each given again when the
        /// copy reaches its tick, before that tick's step as on the host. Ones the restored world already has (numbered up
        /// to its CommandSeq) are dropped.
        /// </summary>
        public readonly SortedDictionary<long, LoggedCommand> Replay = new SortedDictionary<long, LoggedCommand>();

        void PlayReplay()
        {
            while (Replay.Count > 0)
            {
                var c = Replay.First().Value;
                if (c.Tick > World.Tick) return;
                Replay.Remove(c.Seq);
                if (c.Seq <= World.CommandSeq) continue;
                World.CommandSeq = c.Seq;
                Commands.Execute(World, c.Team, c.Cmd);
            }
        }

        void Step()
        {
            if (Replay.Count > 0) PlayReplay();
            foreach (var ai in ais.ToList()) ai.Update(World);
            World.Step();
            HousePolicy();
        }

        /// <summary>A restored world that was a fetch behind the view: run it up to `tick` (no clock, at most 60) and keep the view's 0..1 fraction of a tick.</summary>
        public void CatchUp(int tick, float alpha)
        {
            for (int n = 0; World.Tick < tick && n < 60 && !World.GameOver; n++) Step();
            accumulator = alpha * World.Dt;
        }

        /// <summary>0..1 fraction between the last two ticks, for view interpolation.</summary>
        public float Alpha => accumulator / World.Dt;
    }
}
