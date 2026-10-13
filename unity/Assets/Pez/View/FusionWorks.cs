using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The fusion reactor as a working tokamak (MOTION.md fusion_reactor). While it has plasma to burn (the sim's Working):
    ///  - the plasma ring in the vessel (the model's plasma node) circulates, 10 to 30 rpm with the team's power load, its
    ///    helical strands crawling round under the coils;
    ///  - the pellet injector fires a fuel pellet into the vessel every 3.2 s with nothing drawing, every 1.3 s at full
    ///    load: the magazine's spring pushes the stack (pellets node) up one pellet into the head in 0.16 s, the pellet
    ///    zaps from the muzzle (port) into the ring and the plasma flares;
    ///  - the steam generator puffs from its vent (vent_0), faster under load.
    /// When the plasma runs out the ring quenches (it flickers and fades over 1.5 s and coasts to a stop over 2.5 s), the steam
    /// gives a last puff and the stack sinks into the magazine's base, leaving the cage empty. When plasma comes back the
    /// stack rises (1 s), then the central solenoid strikes the plasma: three arcs from the solenoid into the ring, a
    /// flash, and it spins up over 2 s. An unfinished reactor is dark with an empty magazine, so finishing one is its first light.
    /// A model without those nodes gets the parts it has.
    /// </summary>
    public class FusionWorks
    {
        const float Pitch = 0.085f;      // the pellets' spacing in the magazine (build_models.py S_fusion_reactor)
        const int Pellets = 9;
        const float Ring = 0.78f;        // the plasma ring's radius
        static readonly Color Magenta = new Color(1f, 0.3f, 0.82f, 1f);

        readonly Transform plasma, pellets, port, vent, solenoid;
        readonly Renderer plasmaR;
        readonly Quaternion plasmaRest;
        readonly Vector3 pelletRest;
        readonly float phase;
        float spin, rpm, glow = -1f, shown = -1f, pulse, sink = -1f, stepT = -1f, shotNext, steamNext, strikeT = -1f;
        bool fuelled, on;
        int strikes;

        FusionWorks(Transform model, int seed)
        {
            plasma = PezMotion.FindDeep(model, "plasma");
            pellets = PezMotion.FindDeep(model, "pellets");
            port = PezMotion.FindDeep(model, "port");
            vent = PezMotion.FindDeep(model, "vent_0");
            solenoid = PezMotion.FindDeep(model, "solenoid");
            var r = PezMotion.FindDeep(model, "plasma__M_E_Magenta");
            plasmaR = r ? r.GetComponent<Renderer>() : null;
            if (plasma) { plasmaRest = plasma.localRotation; spin = seed * 47f; }
            if (pellets) pelletRest = pellets.localPosition;
            phase = seed * 0.37f;
        }

        public static FusionWorks For(GameObject model, int seed) => new FusionWorks(model.transform, seed);

        /// <summary>`burning`: complete and the sim says it has plasma; `load`: the team's PowerUsed / PowerProduced;
        /// `effects`: emit steam and arcs (not while it's on fire). Call with Fx.Audience set.</summary>
        public void Tick(bool burning, float load, bool effects, float dt)
        {
            float now = Time.time;
            load = Mathf.Clamp01(load);
            if (sink < 0f)
            {
                // First sight: as it is, no transition.
                fuelled = on = burning; sink = burning ? 0f : 1f; glow = burning ? 1f : 0f;
                rpm = burning ? Mathf.Lerp(10f, 30f, load) : 0f;
                shotNext = now + Random.Range(0.5f, 2f) + phase % 1f;
            }
            if (burning != fuelled)
            {
                fuelled = burning;
                if (!burning)
                {
                    on = false; stepT = -1f;
                    if (effects && vent) Fx.Steam(vent.position, 0.45f); // the last of the heat
                }
            }

            // The magazine: sinks into its base when the plasma runs out (1.2 s), rises again when it's back (1 s).
            sink = Mathf.MoveTowards(sink, fuelled ? 0f : 1f, dt / (fuelled ? 1f : 1.2f));
            if (fuelled && !on && sink <= 0f) { on = true; strikeT = 0f; strikes = 0; } // stack's up: strike the plasma

            // The solenoid strikes the plasma: three arcs 0.12 s apart into the ring, and it flares.
            if (strikeT >= 0f)
            {
                strikeT += dt;
                while (strikes < 3 && strikeT >= strikes * 0.12f)
                {
                    if (effects && solenoid && plasma) Strike(strikes);
                    strikes++;
                    pulse = 1.2f;
                }
                if (strikes >= 3) strikeT = -1f;
            }

            // Pellet shots: the stack steps up one pellet (0.16 s), the head fires it and the ring flares.
            float lift = 0f;
            if (on && stepT < 0f && now >= shotNext) stepT = 0f;
            if (stepT >= 0f)
            {
                stepT += dt;
                float k = Mathf.Clamp01(stepT / 0.16f);
                lift = Pitch * k * k * (3f - 2f * k);
                if (k >= 1f)
                {
                    stepT = -1f; lift = 0f; // the next pellet is where this one was: back to rest, seamlessly
                    if (effects && port && plasma) Fx.Arc(port.position, RingPoint(port.position), Magenta, 0.03f, 0.12f, lamp: false); // no light: a shot every ~2 s mustn't wash the pad
                    pulse = Mathf.Max(pulse, 0.45f);
                    shotNext = now + Mathf.Lerp(3.2f, 1.3f, load) * Random.Range(0.8f, 1.2f);
                }
            }
            if (pellets)
            {
                float s = sink * sink * (3f - 2f * sink);
                pellets.localPosition = pelletRest + Vector3.up * (lift - s * (Pellets - 1) * Pitch);
            }

            // The plasma: circulates with the load while it burns, coasts to a stop when it's out.
            rpm = Mathf.MoveTowards(rpm, on ? Mathf.Lerp(10f, 30f, load) : 0f, dt * (on ? 15f : 12f));
            spin = Mathf.Repeat(spin + rpm * 6f * dt, 360f);
            if (plasma) plasma.localRotation = plasmaRest * Quaternion.Euler(0f, spin, 0f);

            // Its light: full while it burns, flaring on each strike and shot; quenching, it flickers and fades over 1.5 s.
            glow = Mathf.MoveTowards(glow, on ? 1f : 0f, dt / (on ? 0.4f : 1.5f));
            pulse = Mathf.Max(0f, pulse - dt * 2.5f);
            float g = glow + pulse;
            if (!on && glow > 0.05f && Mathf.Repeat(now * 9f + phase, 1f) < 0.3f) g *= 0.35f;
            if (plasmaR && Mathf.Abs(g - shown) > 0.01f)
            {
                PezShade.Set(plasmaR, Mathf.Lerp(0.35f, 1f, Mathf.Clamp01(g)), Mathf.Max(g, 0.03f));
                shown = g;
            }

            // Steam off the generator, faster under load.
            if (on && effects && vent && now >= steamNext)
            {
                Fx.Steam(vent.position, 0.32f);
                steamNext = now + 0.9f / Mathf.Max(load, 0.25f) * Random.Range(0.7f, 1.3f);
            }
        }

        /// <summary>The point on the plasma ring nearest a world point (in the ring's plane).</summary>
        Vector3 RingPoint(Vector3 world)
        {
            var parent = plasma.parent;
            var c = plasma.localPosition;
            var d = parent.InverseTransformPoint(world) - c; d.y = 0f;
            d = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.forward;
            return parent.TransformPoint(c + d * Ring);
        }

        /// <summary>Strike `i` of three: an arc from the rim of the solenoid's cap down into the ring through the crown slot,
        /// in the gaps between the coils (multiples of 45 degrees), roughly a third of the way round from the last.</summary>
        void Strike(int i)
        {
            var parent = plasma.parent;
            var c = plasma.localPosition;
            float a = (i * 135f + Mathf.Floor(Mathf.Repeat(phase * 8f, 8f)) * 45f) * Mathf.Deg2Rad;
            var d = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            var from = parent.InverseTransformPoint(solenoid.position) + d * 0.32f + Vector3.down * 0.07f;
            Fx.Arc(parent.TransformPoint(from), parent.TransformPoint(c + d * Ring + Vector3.up * 0.1f), Magenta, 0.05f, 0.3f);
        }
    }
}
