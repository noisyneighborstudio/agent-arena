using System;
using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public class ReinforcementPlan
    {
        public readonly SortedDictionary<string, int> Keep = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Budget = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedDictionary<string, int> Spent = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public float NextAt;
        public bool BudgetExhausted;

        static JObj Amounts(SortedDictionary<string, int> items) => items.Aggregate(new JObj(), (o, kv) => o.Set(kv.Key, kv.Value));
        public JObj View() => new JObj().Set("keep", Amounts(Keep)).Set("budget", Amounts(Budget)).Set("spent", Amounts(Spent))
            .Set("remaining", Budget.Aggregate(new JObj(), (o, kv) => o.Set(kv.Key, Math.Max(0, kv.Value - (Spent.TryGetValue(kv.Key, out var spent) ? spent : 0)))))
            .Set("exhausted", BudgetExhausted);
        internal JObj Save() => View().Set("next_at", SnapIO.X(NextAt));
        internal static ReinforcementPlan Load(Dictionary<string, object> d)
        {
            if (d == null) return null;
            var plan = new ReinforcementPlan();
            ReadSaved(d.Obj("keep"), plan.Keep); ReadSaved(d.Obj("budget"), plan.Budget); ReadSaved(d.Obj("spent"), plan.Spent);
            d.Load("next_at", ref plan.NextAt); d.Load("exhausted", ref plan.BudgetExhausted);
            return plan;
        }
        static void ReadSaved(Dictionary<string, object> d, SortedDictionary<string, int> target)
        {
            if (d == null) return;
            foreach (var kv in d) target[kv.Key] = SnapIO.ToI(kv.Value);
        }
    }

    public static class ObjectiveReinforcements
    {
        public const float Cadence = 1f;

        static bool Counts(Dictionary<string, object> raw, SortedDictionary<string, int> target)
        {
            if (raw == null || raw.Count == 0) return false;
            foreach (var kv in raw)
            {
                if (!(kv.Value is double n) || double.IsNaN(n) || double.IsInfinity(n) || n != Math.Floor(n) || n < 0 || n > int.MaxValue) return false;
                target[kv.Key] = (int)n;
            }
            return true;
        }

        public static bool Parse(World w, int team, Dictionary<string, object> raw, out ReinforcementPlan plan, out string error)
        {
            plan = null; error = null;
            var parsed = new ReinforcementPlan();
            if (!Counts(raw?.Obj("keep"), parsed.Keep) || !Counts(raw?.Obj("budget"), parsed.Budget))
            { error = "reinforce requires nonempty keep and budget objects with nonnegative integer amounts"; return false; }
            foreach (var cap in parsed.Budget)
                if (!Defs.Items.Contains(cap.Key)) { error = "unknown reinforcement budget resource '" + cap.Key + "'"; return false; }
            foreach (var keep in parsed.Keep)
            {
                var def = w.Def(keep.Key);
                if (def == null || def.IsStructure || def.IsMine || def.BuiltBy == Producer.None || (def.OwnerTeam >= 0 && def.OwnerTeam != team))
                { error = "reinforcement keep must name units your team can train"; return false; }
                if (!def.Cost.Any(c => c.Value > 0 && parsed.Budget.ContainsKey(c.Key)))
                { error = "every reinforcement unit type must consume at least one explicitly capped resource"; return false; }
            }
            plan = parsed; return true;
        }

        static int BudgetCount(ReinforcementPlan plan, EntityDef def, int count)
        {
            if (def == null) return 0;
            foreach (var cap in plan.Budget)
                if (def.Cost.TryGetValue(cap.Key, out var price) && price > 0)
                    count = Math.Min(count, Math.Max(0, cap.Value - (plan.Spent.TryGetValue(cap.Key, out var spent) ? spent : 0)) / price);
            return count;
        }

        public static bool ProducerEligible(World w, int team, ProdItem item, Entity producer)
        {
            if (item.ObjectiveId == null) return true;
            var def = w.Def(item.Key);
            return producer != null && !producer.Dead && producer.Team == team && producer.IsStructure && producer.IsComplete &&
                producer.Id == item.StructureId && def != null && producer.Def.Produces == def.BuiltBy &&
                producer.RallySquad == item.ReinforcementSquad && w.Teams[team].Squads.ContainsKey(item.ReinforcementSquad ?? "");
        }

        public static void ProducerBlocked(World w, int team, ProdItem item)
        {
            if (item.ObjectiveId == null || ProducerEligible(w, team, item, w.Get(item.StructureId))) return;
            var objective = Objectives.ForSquad(w, team, item.ReinforcementSquad);
            if (objective != null)
                Objectives.Pause(w, team, objective, "reinforcement_producer_unavailable", new JObj()
                    .Set("unit", item.Key).Set("producer", item.StructureId).Set("commitment_objective", item.ObjectiveId).Set("queue_preserved", true));
        }

        static bool CommittedToSquad(World w, int team, ProdItem item, string squad)
        {
            if (item.ObjectiveId != null) return item.ReinforcementSquad == squad;
            // Ordinary explicit or already assigned production is known to join this producer's current rally squad.
            // An unassigned shared item has no squad commitment yet; never guess which producer will take it.
            var producer = w.Get(item.StructureId != 0 ? item.StructureId : item.WorkerId);
            var def = w.Def(item.Key);
            return producer != null && producer.Team == team && producer.IsComplete && def != null &&
                producer.Def.Produces == def.BuiltBy && producer.RallySquad == squad;
        }

        static void Exhausted(World w, int team, Objective objective, string key)
        {
            objective.Reinforce.BudgetExhausted = true;
            Objectives.Pause(w, team, objective, "budget_exhausted", new JObj().Set("unit", key).Set("reinforce", objective.Reinforce.View()));
        }

        public static void Tick(World w, int team, Objective objective, Squad squad, List<Entity> live)
        {
            var plan = objective.Reinforce;
            if (plan == null || objective.Paused) return;
            if (plan.BudgetExhausted) { Exhausted(w, team, objective, null); return; }
            if (w.Time < plan.NextAt) return;
            plan.NextAt = w.Time + Cadence;
            var t = w.Teams[team];
            foreach (var keep in plan.Keep)
            {
                int committed = t.UnitQueues.Values.Sum(q => q.Count(p => p.Key == keep.Key && CommittedToSquad(w, team, p, squad.Name)));
                int deficit = keep.Value - live.Count(e => e.Def.Key == keep.Key) - committed;
                if (deficit <= 0) continue;
                var def = w.Def(keep.Key);
                if (def == null)
                {
                    Objectives.Pause(w, team, objective, "reinforcement_unavailable", new JObj().Set("unit", keep.Key));
                    return;
                }
                int count = BudgetCount(plan, def, Math.Min(10, deficit));
                if (count == 0) { Exhausted(w, team, objective, keep.Key); return; }
                var producers = w.Owned(team).Where(e => e.IsStructure && e.IsComplete && e.Def.Produces == def.BuiltBy && e.RallySquad == squad.Name)
                    .OrderBy(e => e.Id).ToList();
                if (producers.Count == 0)
                {
                    Objectives.Pause(w, team, objective, "reinforcement_producer_unavailable", new JObj().Set("unit", keep.Key).Set("squad", squad.Name));
                    return;
                }
                if (w.MissingPrereq(team, def) is string missing)
                {
                    Objectives.Pause(w, team, objective, "reinforcement_unavailable", new JObj().Set("unit", keep.Key).Set("reason", missing));
                    return;
                }
                var queue = t.UnitQueues[def.BuiltBy]; int bought = 0;
                for (int i = 0; i < count; i++)
                {
                    // Balance commitments among only the machines the agent already rallied here; ids break ties.
                    var producer = producers.OrderBy(e => t.UnitQueues.Values.Sum(q => q.Count(p => p.StructureId == e.Id || p.WorkerId == e.Id)))
                        .ThenBy(e => e.Id).First();
                    int before = queue.Count;
                    // Ordinary named purchases: reserves constrain converters, while training still spends.
                    Commands.Train(w, team, new Dictionary<string, object> { ["type"] = "train", ["unit"] = keep.Key,
                        ["count"] = 1d, ["structure_id"] = (double)producer.Id });
                    if (queue.Count == before) break; // stock shortage never selects a different type or invents resources
                    foreach (var item in queue.Skip(before))
                    {
                        item.ObjectiveId = objective.Id; item.ReinforcementSquad = squad.Name;
                        foreach (var cost in def.Cost) plan.Spent[cost.Key] = (plan.Spent.TryGetValue(cost.Key, out var spent) ? spent : 0) + cost.Value;
                        bought++;
                    }
                }
                if (bought == 0) continue; // stock shortage waits; it neither spends nor invents an alternative
                if (!plan.Keep.Any(k => k.Value > 0 && BudgetCount(plan, w.Def(k.Key), 1) > 0)) { Exhausted(w, team, objective, keep.Key); return; }
            }
        }
    }
}
