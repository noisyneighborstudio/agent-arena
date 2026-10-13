using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The composite foundry at work (MOTION.md composite_foundry): one press cycle for every composite the sim makes, so
    /// 2 s at full power and 4 s on low power (the sim halves it). Each cycle:
    ///  - a steel blank (the model's blank node) slides out of the furnace house's feed slot (press_feed) onto the bed
    ///    (press_bed);
    ///  - the ram drops fast, slows for the last of its stroke and clamps the blank; its seam glows hot and steam hisses
    ///    out at both sides of the platen; under it the blank fuses to black composite;
    ///  - the ram lifts, the tile slides along the rails onto the stack in the magazine (press_out) and the stack steps
    ///    down a tile with it (the stack node; it's back where it started, one tile richer, when the cycle ends);
    ///  - the grape vents (vent_0, vent_1) puff one after the other.
    /// The furnace's peephole glows while the foundry works, at half heat on low power. Out of steel or crystal the press
    /// finishes its cycle and stops with the ram up and the bed empty; the peephole cools and the vents give a thin wisp
    /// every few seconds. A burning foundry's smoke is the fire's: no steam or puffs. A model without the nodes gets the
    /// parts it has.
    /// </summary>
    public class FoundryWorks
    {
        // The cycle, in fractions of it: feed, ram down (fast, then the slow last tenth), clamped, ram up, out, the stack steps.
        const float FeedEnd = 0.16f, Approach = 0.25f, Clamp = 0.31f, Fuse = 0.45f, LiftStart = 0.58f, LiftEnd = 0.72f, OutEnd = 0.86f;
        const float VentA = 0.62f, VentB = 0.70f;
        const float Tile = 0.04f, Step = 0.06f; // the blank's thickness; the stack's pitch (model units)

        readonly Transform root, ram, blank, stack;
        readonly Transform[] vents;
        readonly Renderer seam, peephole, steel, composite;
        readonly Vector3 ramRest, ramDown, feed, bed, outAt, stackRest, steamA, steamB;
        readonly float phase;
        float u, heat, furnace = -1f, seamShown = -1f, furnaceShown = -1f, wispNext;
        bool running, fused;

        FoundryWorks(Transform model, int seed)
        {
            root = model;
            phase = seed * 0.73f;
            ram = PezMotion.FindDeep(model, "ram");
            blank = PezMotion.FindDeep(model, "blank");
            stack = PezMotion.FindDeep(model, "stack");
            var v = new List<Transform>();
            for (Transform t; (t = PezMotion.FindDeep(model, "vent_" + v.Count)) != null; ) v.Add(t);
            vents = v.ToArray();
            seam = R(model, "ram__M_E_Amber");
            peephole = R(model, "stage_1__M_E_Amber");
            steel = R(model, "blank__M_SpringSteel");
            composite = R(model, "blank__M_Licorice");
            var bedM = PezMotion.FindDeep(model, "press_bed");
            var outM = PezMotion.FindDeep(model, "press_out");
            if (ram && bedM)
            {
                ramRest = ram.localPosition;
                ramDown = ramRest;
                ramDown.y = ram.parent.InverseTransformPoint(bedM.position).y + Tile; // the platen's underside on the blank
                steamA = model.InverseTransformPoint(bedM.position) + new Vector3(0.3f, 0.06f, 0f);
                steamB = model.InverseTransformPoint(bedM.position) + new Vector3(-0.3f, 0.06f, 0f);
            }
            if (blank && bedM && outM)
            {
                feed = blank.localPosition;
                bed = blank.parent.InverseTransformPoint(bedM.position);
                outAt = blank.parent.InverseTransformPoint(outM.position);
            }
            else blank = null;
            if (stack) stackRest = stack.localPosition;
            wispNext = Time.time + Random.Range(1f, 4f);
            Show(false);
        }

        static Renderer R(Transform model, string name)
        {
            var t = PezMotion.FindDeep(model, name);
            return t ? t.GetComponent<Renderer>() : null;
        }

        public static FoundryWorks For(GameObject model, int seed) => new FoundryWorks(model.transform, seed);

        void Show(bool composite)
        {
            fused = composite;
            if (steel) steel.enabled = !composite;
            if (this.composite) this.composite.enabled = composite;
        }

        static float Ease(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));

        /// <summary>`working`: the sim says it's making composite; `low`: the team is on low power; `quiet`: no steam or smoke
        /// (it's burning); `audience`: who may see its effects (WorldView.AudienceFor).</summary>
        public void Tick(bool working, bool low, bool quiet, List<int> audience, float dt)
        {
            float now = Time.time, was = u;
            if (!running && working) { running = true; u = was = 0f; }
            bool wrapped = false;
            if (running)
            {
                u += dt * 0.5f * (low ? 0.5f : 1f);
                if (u >= 1f)
                {
                    wrapped = true;
                    if (working) u -= 1f; else { u = 0f; running = false; }
                }
            }
            float x = running ? u : 0f;

            // The ram: fast approach, the slow last tenth of the stroke, held, then up.
            if (ram)
            {
                float k = x < FeedEnd ? 0f
                        : x < Approach ? 0.9f * Mathf.Pow((x - FeedEnd) / (Approach - FeedEnd), 2f)
                        : x < Clamp ? 0.9f + 0.1f * Ease(Approach, Clamp, x)
                        : x < LiftStart ? 1f
                        : 1f - Ease(LiftStart, LiftEnd, x);
                ram.localPosition = Vector3.LerpUnclamped(ramRest, ramDown, k);
            }
            // The blank: out of the slot, on the bed, along the rails, down with the stack. It fuses under the platen.
            if (blank)
            {
                Vector3 p = x < FeedEnd ? Vector3.Lerp(feed, bed, Ease(0f, FeedEnd, x))
                          : x < LiftEnd ? bed
                          : x < OutEnd ? Vector3.Lerp(bed, outAt, Ease(LiftEnd, OutEnd, x))
                          : outAt + Vector3.down * Step * Ease(OutEnd, 1f, x);
                if (wrapped || !running) p = feed;
                blank.localPosition = p;
                bool f = running && x >= Fuse && !wrapped;
                if (f != fused) Show(f);
            }
            if (stack) stack.localPosition = stackRest + Vector3.down * (running && !wrapped && x >= OutEnd ? Step * Ease(OutEnd, 1f, x) : 0f);

            // The seam heats while it clamps and cools after; the furnace's peephole glows while it works.
            heat = Mathf.MoveTowards(heat, running && x >= Approach && x < LiftStart ? 1f : 0f, dt / (running && x < LiftStart ? 0.15f : 0.9f));
            float hot = low ? 0.6f : 1f;
            float s = Mathf.Lerp(0.1f, hot, heat);
            if (seam && Mathf.Abs(s - seamShown) > 0.005f) { PezShade.Set(seam, Dim(s), s); seamShown = s; }
            float want = working ? (low ? 0.55f : 1f) : 0.1f;
            furnace = furnace < 0f ? want : Mathf.MoveTowards(furnace, want, dt / 1.5f);
            float g = furnace * (working ? 0.9f + 0.1f * Mathf.Sin(now * 11f + phase) * Mathf.Sin(now * 4.3f + phase * 2f) : 1f);
            if (peephole && Mathf.Abs(g - furnaceShown) > 0.005f) { PezShade.Set(peephole, Dim(g), g); furnaceShown = g; }

            if (quiet) return;
            Fx.Audience = audience;
            if (running && !wrapped)
            {
                if (ram && was < Clamp && x >= Clamp) { Fx.Steam(root.TransformPoint(steamA), 0.2f); Fx.Steam(root.TransformPoint(steamB), 0.2f); }
                if (vents.Length > 0 && was < VentA && x >= VentA) Fx.StackSmoke(vents[0].position, low ? 0.6f : 1f);
                if (vents.Length > 1 && was < VentB && x >= VentB) Fx.StackSmoke(vents[1].position, low ? 0.6f : 1f);
            }
            else if (!running && vents.Length > 0 && now >= wispNext)
            {
                Fx.StackSmoke(vents[Random.Range(0, vents.Length)].position, 0f); // banked: a thin wisp
                wispNext = now + Random.Range(3f, 6f);
            }
            Fx.Audience = null;
        }

        static float Dim(float glow) => Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.25f, 1f, glow)); // the lamp body darkens with its light
    }
}
