using System.Collections.Generic;
using System.Linq;

namespace Pez.Sim
{
    public static partial class SquadGuards
    {
        /// <summary>Sets literal retreat goals. The shared guard writer issues the orders after all guards evaluate.</summary>
        static void EvaluateRetreats(World w, int team, Squad squad, SquadGuard guard, List<Entity> live)
        {
            if (!guard.HasTo) return;
            var eligible = live.Where(e => CanControl(w, e)).ToList();
            if (guard.WithdrawBelowStrengthPct > 0 && squad.FormationHp > 0)
            {
                float hp = live.Sum(e => e.Hp);
                float strength = hp / squad.FormationHp * 100f;
                bool triggered = !guard.StrengthWithdrawn && strength < guard.WithdrawBelowStrengthPct;
                if (triggered) { guard.StrengthWithdrawn = true; guard.StrengthAt = w.Time; }
                if (guard.StrengthWithdrawn)
                {
                    var assigned = eligible.Where(e => { var u = State(guard, e); return !u.StrengthExempt && !u.StrengthAssigned; }).ToList();
                    if (assigned.Count > 0)
                    {
                        var slots = Slots(w, team, assigned, guard.To);
                        foreach (var unit in assigned)
                        {
                            var u = State(guard, unit);
                            u.StrengthAssigned = true; u.StrengthGoal = slots[unit];
                        }
                    }
                    if (triggered || assigned.Count > 0)
                    {
                        var facts = new JObj().Set("reason", triggered ? "strength_below_threshold" : "withdrawal_still_latched")
                            .Set("units", assigned.Select(e => (object)e.Id).ToList()).Set("hp", hp).Set("formation_hp", squad.FormationHp)
                            .Set("strength_pct", strength).Set("threshold_pct", guard.WithdrawBelowStrengthPct).Set("to", SnapIO.V(guard.To));
                        Record(w, team, squad, "guard_triggered", "withdraw_below_strength_pct", facts);
                        if (triggered) Record(w, team, squad, "decision_needed", "withdraw_below_strength_pct", facts, Sev.Critical);
                    }
                }
            }

            if (guard.RetreatBelowPct <= 0) return;
            foreach (var unit in live)
            {
                var u = State(guard, unit);
                float hpPct = unit.Hp / unit.Def.MaxHp * 100f;
                // Rearming never returns a healed unit to its interrupted order: its retreat goal remains latched.
                if (hpPct >= guard.RetreatBelowPct + 5f && w.Time - u.HpAt >= Cooldown) u.HpSpent = false;
            }
            var retreating = eligible.Where(e =>
            {
                var u = State(guard, e);
                return !(guard.StrengthWithdrawn && !u.StrengthExempt) && !u.HpRetreated && !u.HpSpent &&
                    e.Hp / e.Def.MaxHp * 100f < guard.RetreatBelowPct && w.Time - u.HpAt >= Cooldown;
            }).ToList();
            if (retreating.Count == 0) return;
            var retreatSlots = Slots(w, team, retreating, guard.To);
            foreach (var unit in retreating)
            {
                var u = State(guard, unit);
                u.HpRetreated = u.HpSpent = true; u.HpAt = w.Time; u.HpGoal = retreatSlots[unit];
                Record(w, team, squad, "guard_triggered", "retreat_below_pct", new JObj().Set("reason", "hp_below_threshold")
                    .Set("units", new List<object> { unit.Id }).Set("hp", unit.Hp).Set("max_hp", unit.Def.MaxHp)
                    .Set("hp_pct", unit.Hp / unit.Def.MaxHp * 100f).Set("threshold_pct", guard.RetreatBelowPct)
                    .Set("rearm_above_pct", guard.RetreatBelowPct + 5f).Set("cooldown_s", Cooldown).Set("to", SnapIO.V(guard.To)));
            }
        }
    }
}
