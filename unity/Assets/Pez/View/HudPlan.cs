using System;
using System.Collections.Generic;
using System.Linq;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    public partial class Hud
    {
        bool showPlan;
        readonly Dictionary<int, Vector2> planScroll = new Dictionary<int, Vector2>();

        float PlanTabs(float x, float y, float width)
        {
            float half = (width - 8) / 2;
            if (Button(new Rect(x, y, half, 30), "Base", !showPlan, true, false, 13)) showPlan = false;
            if (Button(new Rect(x + half + 8, y, half, 30), "Plan", showPlan, true, false, 13)) showPlan = true;
            return y + 42;
        }

        struct PlanRow { public string Text; public bool Head, Warning; }

        List<PlanRow> PlanRows(Sim.Team team)
        {
            var rows = new List<PlanRow>();
            void Add(string text, bool head = false, bool warning = false) => rows.Add(new PlanRow { Text = text, Head = head, Warning = warning });
            Add($"STRATEGY · PLAN {team.PlanVersion}", true);
            Add(string.IsNullOrEmpty(team.StrategyText) ? "No strategy recorded." : team.StrategyText);
            foreach (var priority in team.Priorities) Add("• " + priority);
            Add("OBJECTIVES", true);
            if (team.Objectives.Count == 0) Add("No objectives configured.");
            foreach (var o in team.Objectives.Values)
            {
                Add($"{o.Id} · {o.Squad} · {o.Phase.Replace('_', ' ')}", false, o.Paused);
                bool patrolReleased = o.Verb == "patrol" && (!o.HasAssembly || o.Assembled);
                var at = patrolReleased ? o.Anchor : o.At;
                string target = o.Verb == "escort" ? (o.TargetSquad != null ? "squad " + o.TargetSquad : "unit #" + o.TargetId)
                    : $"{(o.Verb == "patrol" ? patrolReleased ? $"Waypoint {o.PatrolIndex + 1}/{o.Waypoints.Count} · " : "Route starts · " : "")}{StateView.Sector(at)} ({at.X:0.#}, {at.Y:0.#})";
                target += $" · radius {o.Radius:0.#}";
                Add(o.Verb.Replace('_', ' ') + " · " + target);
                if (o.Paused && !string.IsNullOrEmpty(o.PauseReason)) Add("Paused: " + o.PauseReason.Replace('_', ' '), false, true);
                if (o.HasAssembly && !o.Assembled) Add($"Assembling {o.AssemblyMin} at {StateView.Sector(o.AssemblyAt)}");
                if (o.Reinforce != null) Add("Budget: " + string.Join(", ", o.Reinforce.Budget.Select(b => $"{b.Key} { (o.Reinforce.Spent.TryGetValue(b.Key, out var spent) ? spent : 0)}/{b.Value}")));
            }
            Add("LATEST GUARD INTERVENTIONS", true);
            var notes = team.Squads.Values.SelectMany(s => s.Guards.Notes).Select(n => Json.Parse(n) as Dictionary<string, object>)
                .Where(n => n != null).OrderByDescending(n => n.Num("t", 0)).Take(8).ToList();
            if (notes.Count == 0) Add("No guard interventions yet.");
            foreach (var note in notes)
            {
                Add($"{FormatTime(note.Num("t", 0))} · {note.Str("squad")} · {note.Str("rule", "guard").Replace('_', ' ')}");
                Add($"{note.Str("action", "")} · {note.Str("reason", note.Str("kind", "")).Replace('_', ' ')}", false, note.Str("kind") == "decision_needed");
            }
            return rows;
        }

        // Authored text is literal, measured without markup, and physically clipped by the scroll viewport.
        void PlanBody(Rect viewport, Sim.Team team)
        {
            if (viewport.height < 24) return;
            uiRects.Add(viewport);
            var style = new GUIStyle(St(fSans, 14, TextAnchor.UpperLeft, true)) { richText = false, clipping = TextClipping.Clip };
            var heading = new GUIStyle(style) { font = fSansBold };
            float width = Mathf.Max(24, viewport.width - 22), total = 0;
            var rows = PlanRows(team);
            var heights = rows.Select(row => Mathf.Max(18, (row.Head ? heading : style).CalcHeight(new GUIContent(row.Text), width * k) / k)).ToList();
            for (int i = 0; i < rows.Count; i++) total += heights[i] + (rows[i].Head ? 10 : 7);
            planScroll.TryGetValue(team.Id, out var scroll);
            scroll = GUI.BeginScrollView(P(viewport), scroll, new Rect(0, 0, width * k, total * k), false, false);
            float y = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i]; var font = row.Head ? heading : style;
                font.normal.textColor = F(row.Warning ? Amber : row.Head ? Muted : Soft);
                if (Repaint) GUI.Label(P(new Rect(0, y, width, heights[i])), row.Text, font);
                y += heights[i] + (row.Head ? 10 : 7);
            }
            GUI.EndScrollView();
            planScroll[team.Id] = scroll;
        }
    }
}
