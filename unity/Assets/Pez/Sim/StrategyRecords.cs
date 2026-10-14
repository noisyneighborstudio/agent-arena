using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    /// <summary>Descriptive intent only. No sim step or executor reads strategy prose or priorities.</summary>
    public static class StrategyRecords
    {
        public const int MaxText = 2000, MaxPriorities = 16, MaxPriority = 120;
        public static JObj View(Team team) => new JObj().Set("text", team.StrategyText).Set("priorities", team.Priorities.Cast<object>().ToList());

        public static JObj Configure(World w, int team, Dictionary<string, object> c)
        {
            if (!c.TryGetValue("text", out var textValue) || !(textValue is string text) || text.Length > MaxText)
                return Fail("set_strategy requires text as a string of at most 2000 characters");
            if (!c.TryGetValue("priorities", out var raw) || !(raw is List<object> items) || items.Count > MaxPriorities ||
                items.Any(value => !(value is string priority) || priority.Length > MaxPriority))
                return Fail("set_strategy requires priorities as at most 16 strings, each at most 120 characters");
            text = text.Trim();
            var priorities = items.Cast<string>().Select(p => p.Trim()).ToList();
            var t = w.Teams[team];
            bool changed = text != t.StrategyText || !priorities.SequenceEqual(t.Priorities);
            if (changed)
            {
                t.StrategyText = text; t.Priorities.Clear(); t.Priorities.AddRange(priorities); t.PlanVersion++;
            }
            return new JObj().Set("ok", true).Set("strategy", View(t)).Set("plan_version", t.PlanVersion).Set("changed", changed)
                .Set("result", changed ? "strategy recorded; descriptive text does not execute" : "strategy unchanged");
        }

        static JObj Fail(string message) => new JObj().Set("ok", false).Set("error", message);
    }
}
