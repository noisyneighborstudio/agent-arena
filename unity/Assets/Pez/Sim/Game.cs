using System.Collections.Generic;

namespace Pez.Sim
{
    public class GameConfig
    {
        public int Seed = 1337;
        public int MapSize = 64;
        public int StartCredits = 5000;
        public float Speed = 1f;
        /// <summary>Per team: human | ai | llm</summary>
        public string[] Controllers = { "human", "ai" };
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
            World = new World(cfg.Controllers.Length, cfg.Seed, cfg.MapSize, cfg.StartCredits);
            ais.Clear();
            for (int i = 0; i < cfg.Controllers.Length; i++)
            {
                World.Teams[i].Controller = cfg.Controllers[i];
                if (cfg.Controllers[i] == "ai") { ais.Add(new SimpleAI(i)); World.Teams[i].PlayerName = "Scripted AI"; }
            }
            accumulator = 0;
        }

        /// <summary>Advance by real elapsed seconds. Returns ticks stepped.</summary>
        public int Advance(float realDt)
        {
            if (Paused || World.GameOver) return 0;
            accumulator += realDt * Speed;
            int steps = 0;
            while (accumulator >= World.Dt && steps < 10)
            {
                accumulator -= World.Dt;
                foreach (var ai in ais) ai.Update(World);
                World.Step();
                steps++;
            }
            if (steps == 10) accumulator = 0; // don't spiral if we fall behind
            return steps;
        }

        /// <summary>0..1 fraction between the last two ticks, for view interpolation.</summary>
        public float Alpha => accumulator / World.Dt;
    }
}
