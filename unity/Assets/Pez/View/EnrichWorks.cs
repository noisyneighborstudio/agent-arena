using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The enrichment plant's centrifuge hall (MOTION.md enrichment_plant), run from the sim: one cycle per plasma it makes,
    /// so 2.5 s, and 5 s on low power (the sim halves production).
    ///  - The cascade (rotor_0..5) spins under the skylight while the plant works: up to speed in about 1.5 s, at half speed
    ///    on low power, where it also sags now and then as the supply browns out; starved, it coasts to a stop over 3 s.
    ///  - Each cycle a slug of uranium runs out of the hopper: the heap (ore) slumps a little and settles, and the sight glass
    ///    on the hopper's pedestal (sight) flashes as it passes. At the end of the cycle a plasma brick comes up into the
    ///    dispenser stem from below and pushes the stack up a slot (mag_0..3), and the exhaust vent (vent) puffs.
    ///  - Every fourth brick the team flip-top head pops open, the stack empties from the top, a brick (candy) flips out over
    ///    the lip and away, and the head drops shut with a bounce.
    ///  - Starved (the sim's Working is false: out of uranium), the cycle in hand finishes, the rotors coast down, the hopper's
    ///    heap drains out of sight and the amber lamp (lamp_wait) blinks.
    ///  - Glow is state: the sight glass is lit while ore runs (dim at rest, dark while starved), the amber lamp only while
    ///    it waits for uranium. Both are dark until it's built; under fog it holds still and they're neutral.
    /// It owns the sight glass and the lamp: For() takes them out of WorldView's generic power glow (which still dims the ore
    /// and the bricks with the power). A model without these nodes gets the parts it has.
    /// </summary>
    public class EnrichWorks
    {
        const int Batch = 4;
        const float Rate = 0.4f;                    // cycles a second at full power (the recipe's rate)
        const float FeedU = 0.12f, PushU = 0.78f;   // in a cycle: the slug runs at 0.12, the brick comes up from 0.78
        const float Full = 450f;                    // the cascade's speed at full power, deg/s
        const float Pitch = 0.12f;                  // the stack's slot pitch (model units)

        readonly Transform[] rotors, mags;
        readonly Transform head, candy, ore, vent;
        readonly Quaternion[] rotorRest;
        readonly float[] angle;
        readonly Vector3[] slots;
        readonly Vector3 oreRest, candyRest, candyOut;
        readonly Quaternion headRest, candyRot;
        readonly Renderer sightLamp, waitLamp;
        readonly float phase;
        float u, spin, slump, drain, popT = -1f, flash, shownSight = -1f, shownWait = -1f;
        int stacked;
        bool running, dispensed;

        EnrichWorks(Transform model, int seed, ref Renderer[] glows)
        {
            rotors = Numbered(model, "rotor_", 8);
            mags = Numbered(model, "mag_", Batch);
            head = PezMotion.FindDeep(model, "head");
            candy = PezMotion.FindDeep(model, "candy");
            ore = PezMotion.FindDeep(model, "ore");
            vent = PezMotion.FindDeep(model, "vent");
            rotorRest = new Quaternion[rotors.Length];
            angle = new float[rotors.Length];
            for (int i = 0; i < rotors.Length; i++) { rotorRest[i] = rotors[i].localRotation; angle[i] = (seed * 47 + i * 71) % 360; }
            slots = new Vector3[mags.Length];
            for (int i = 0; i < mags.Length; i++) slots[i] = mags[i].localPosition;
            if (ore) oreRest = ore.localPosition;
            if (head) headRest = head.localRotation;
            if (candy)
            {
                candyRest = candy.localPosition; candyRot = candy.localRotation;
                // Out over the lip: away from the head's hinge.
                var d = head ? candyRest - head.localPosition : Vector3.back;
                d.y = 0f;
                candyOut = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.back;
                candy.gameObject.SetActive(false);
            }
            var s = Lights(model, "sight"); sightLamp = s.Length > 0 ? s[0] : null;
            var w = Lights(model, "lamp_wait"); waitLamp = w.Length > 0 ? w[0] : null;
            // These lights say the plant's state, so WorldView's power glow leaves them alone.
            if (glows != null) glows = System.Array.FindAll(glows, r => r != sightLamp && r != waitLamp);
            phase = seed * 0.37f;
            stacked = Mathf.Abs(seed) % Batch;          // plants don't all pop together
            u = Mathf.Repeat(seed * 0.29f, 1f) * PushU;  // and each starts mid-cycle, before its brick comes up
            Pose(0f);
        }

        public static EnrichWorks For(GameObject model, int seed, ref Renderer[] glows) => new EnrichWorks(model.transform, seed, ref glows);

        static Transform[] Numbered(Transform model, string prefix, int max)
        {
            var list = new System.Collections.Generic.List<Transform>();
            for (Transform c; list.Count < max && (c = PezMotion.FindDeep(model, prefix + list.Count)) != null; ) list.Add(c);
            return list.ToArray();
        }

        static Renderer[] Lights(Transform model, string node)
        {
            var n = PezMotion.FindDeep(model, node);
            return n ? PezShade.Emissive(n.gameObject) : new Renderer[0];
        }

        /// <summary>
        /// `complete`: built; `working`: the sim's Working (it made plasma this tick); `sees`: the local view sees its live
        /// state (else it holds still, its lights neutral); `fumes`: not on fire (a burning plant's smoke is the fire's).
        /// Effects go to Fx.Audience, which the caller sets.
        /// </summary>
        public void Tick(Team team, bool complete, bool working, bool sees, bool fumes, float dt)
        {
            if (!sees) { Lamps(0.8f, 0.25f); return; } // remembered under fog: no live state
            bool low = team.LowPower;
            working &= complete;
            float now = Time.time, rate = low ? 0.5f : 1f;

            // The cycle: runs while working; starved, it finishes the one in hand and stops at its start.
            if (working) running = true;
            if (running)
            {
                float was = u;
                u += dt * Rate * rate;
                if (was < FeedU && u >= FeedU) { flash = 1f; slump = 1f; }
                if (u >= 1f)
                {
                    u -= 1f;
                    stacked++;
                    if (fumes && vent) Fx.Steam(vent.position, low ? 0.1f : 0.14f);
                    if (stacked >= Batch && popT < 0f) { popT = 0f; dispensed = false; }
                    if (!working) { running = false; u = 0f; }
                }
            }

            // The cascade: up to speed in ~1.5 s, coasting down over ~3 s. On low power it runs at half speed and sags
            // for half a second every 2-3 s as the supply browns out.
            float want = 0f;
            if (working)
            {
                want = Full * rate;
                if (low) { float c = Mathf.Repeat(now / 2.6f + phase, 1f) * 2.6f; if (c < 0.5f) want *= 0.35f; }
            }
            spin = Mathf.MoveTowards(spin, want, dt * (want > spin ? Full / 1.5f : Full / 3f));
            for (int i = 0; i < rotors.Length; i++)
            {
                angle[i] = Mathf.Repeat(angle[i] + spin * (1f + 0.04f * (i % 3)) * dt, 360f);
                rotors[i].localRotation = rotorRest[i] * Quaternion.Euler(0f, angle[i], 0f);
            }

            // The hopper: the heap slumps as the slug runs and settles; starved, it drains out of sight.
            slump = Mathf.MoveTowards(slump, 0f, dt / 0.8f);
            drain = Mathf.MoveTowards(drain, complete && !working && !running ? 1f : 0f, dt / (working ? 0.6f : 1.5f));
            if (ore) ore.localPosition = oreRest + Vector3.down * (0.015f * Mathf.Sin(Mathf.PI * slump) + 0.14f * drain);

            Pose(running ? (u < PushU ? 0f : Mathf.SmoothStep(0f, 1f, (u - PushU) / (1f - PushU))) : 0f);
            if (popT >= 0f) Pop(dt);

            // Glow is state.
            flash = Mathf.MoveTowards(flash, 0f, dt / 0.5f);
            if (!complete) Lamps(0.05f, 0.05f);
            else if (working) Lamps(Mathf.Max(low ? 0.3f : 0.45f, flash * (low ? 0.6f : 1f)), 0.1f);
            else Lamps(Mathf.Max(0.08f, flash), Mathf.Repeat(now * 0.9f + phase, 1f) < 0.65f ? 1f : 0.2f);
        }

        /// <summary>The stack in the stem: `k` is how far the brick coming up from below (and the stack on it) has risen.</summary>
        void Pose(float k)
        {
            // While a brick comes up the stack has one more in it, every brick a slot lower until it's pushed up.
            int n = Mathf.Min(stacked + (k > 0f ? 1 : 0), Batch);
            if (popT >= 0.08f && !dispensed) n = Mathf.Max(0, Mathf.Min(stacked, Batch) - 1 - (int)((popT - 0.08f) / 0.035f));
            float drop = k > 0f && stacked < Batch ? Pitch * (1f - k) : 0f;
            for (int i = 0; i < mags.Length; i++)
            {
                bool show = i < n;
                if (mags[i].gameObject.activeSelf != show) mags[i].gameObject.SetActive(show);
                if (show) mags[i].localPosition = slots[i] + Vector3.down * drop;
            }
        }

        /// <summary>The dispenser pops: the head snaps open, the stack goes up and out, one brick flips away over the lip,
        /// and the head drops shut with a bounce.</summary>
        void Pop(float dt)
        {
            popT += dt;
            float t = popT;
            if (head)
            {
                float a;
                if (t < 0.1f) a = Mathf.SmoothStep(0f, 1f, t / 0.1f);
                else if (t < 0.4f) a = 1f;
                else { float v = t - 0.4f; a = Mathf.Abs(Mathf.Cos(v * 7f)) * Mathf.Exp(-v * 5f); }
                head.localRotation = headRest * Quaternion.Euler(40f * a, 0f, 0f);
            }
            if (t >= 0.08f + 0.035f * Batch && !dispensed) { stacked = Mathf.Max(0, stacked - Batch); dispensed = true; }
            if (candy)
            {
                float f = (t - 0.1f) / 0.8f;
                bool fly = f > 0f && f < 1f;
                if (candy.gameObject.activeSelf != fly) candy.gameObject.SetActive(fly);
                if (fly)
                {
                    candy.localPosition = candyRest + candyOut * (0.3f * f) + Vector3.up * (0.45f * Mathf.Sin(Mathf.PI * f * 0.8f) - 0.1f * f);
                    candy.localRotation = candyRot * Quaternion.Euler(-260f * f, 120f * f, 0f);
                    candy.localScale = Vector3.one * (f < 0.65f ? 1f : Mathf.Lerp(1f, 0f, (f - 0.65f) / 0.35f));
                }
            }
            if (t > 1.5f)
            {
                if (head) head.localRotation = headRest;
                popT = -1f;
                if (stacked >= Batch) { popT = 0f; dispensed = false; } // a batch filled while it was popping
            }
        }

        void Lamps(float sight, float wait)
        {
            if (Mathf.Abs(sight - shownSight) > 0.005f) { Light(sightLamp, sight); shownSight = sight; }
            if (wait != shownWait) { Light(waitLamp, wait); shownWait = wait; }
        }

        // The lamp body darkens with its light, as WorldView's power glow does.
        static void Light(Renderer r, float g) { if (r) PezShade.Set(r, Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.25f, 1f, g)), g); }
    }
}
