using System.Collections.Generic;

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
    }

    /// <summary>Owns a World plus who controls each team, and advances it in real time.</summary>
    public class Game
    {
        public World World { get; private set; }
        public GameConfig Config { get; private set; }
        public float Speed;
        public bool Paused;
        readonly List<SimpleAI> ais = new List<SimpleAI>();
        float accumulator;

        public Game(GameConfig cfg) { Restart(cfg); }

        public void Restart(GameConfig cfg)
        {
            Config = cfg;
            Speed = cfg.Speed;
            World = new World(cfg.Controllers.Length, cfg.Seed, cfg.MapSize) { Open = cfg.Open, MaxPlayers = cfg.MaxPlayers, MaxMapSize = cfg.MaxMapSize };
            ais.Clear();
            for (int i = 0; i < cfg.Controllers.Length; i++)
            {
                World.Teams[i].Controller = cfg.Controllers[i];
                if (cfg.Controllers[i] == "ai") { ais.Add(new SimpleAI(i)); World.Teams[i].PlayerName = "Scripted AI"; }
                if (cfg.Orders != null && i < cfg.Orders.Length && !string.IsNullOrWhiteSpace(cfg.Orders[i])) World.SetOrders(i, cfg.Orders[i]);
            }
            accumulator = 0;
        }

        /// <summary>Advance by real elapsed seconds. Returns ticks stepped.</summary>
        public int Advance(float realDt)
        {
            if (Paused || World.GameOver) return 0;
            accumulator += realDt * Speed;
            int steps = 0;
            // Generous catch-up so a throttled frame rate (window hidden, App Nap) doesn't slow the game clock.
            while (accumulator >= World.Dt && steps < 100)
            {
                accumulator -= World.Dt;
                foreach (var ai in ais) ai.Update(World);
                World.Step();
                steps++;
            }
            if (steps == 100) accumulator = 0; // don't spiral if we fall far behind
            return steps;
        }

        /// <summary>0..1 fraction between the last two ticks, for view interpolation.</summary>
        public float Alpha => accumulator / World.Dt;
    }
}
