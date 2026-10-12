using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The power plant as a spring under load (MOTION.md power_plant; base-building review, hero idea). From the team's
    /// power load (PowerUsed / PowerProduced):
    ///  - the cream springs round both towers (the model's coil_0, coil_1, pivots at their feet) are pressed down by their
    ///    collars as the base draws more: 100% of their height with nothing drawing, 78% at full load, eased over 0.4 s.
    ///    On low power (the base draws more than it makes) they bottom out at 70% and chatter at 4 Hz.
    ///  - the flywheel on the generator house turns with the load, 40 to 140 rpm; on low power it labours, surging and
    ///    stalling every second or so.
    ///  - electric arcs jump between the lips of the two stacks (stack_0, stack_1), one in four down from a stack to an
    ///    insulator on the transformer (insulator_N): every 5 to 8 s with nothing drawing, every 1.5 to 2.5 s at full load;
    ///    on low power they come in stuttering bursts of two or three.
    /// A model without those nodes gets the parts it has.
    /// </summary>
    public class PowerWorks
    {
        readonly Transform[] coils, stacks, insulators;
        readonly Transform flywheel;
        readonly Quaternion wheelRest;
        readonly float phase;
        float squeeze = -1f, angle, rpm, arcNext = -1f;
        int burst;
        static readonly Color ArcCyan = new Color(0.45f, 0.92f, 1f, 1f);

        PowerWorks(Transform model, int seed)
        {
            var list = new System.Collections.Generic.List<Transform>();
            for (Transform c; (c = PezMotion.FindDeep(model, "coil_" + list.Count)) != null; ) list.Add(c);
            coils = list.ToArray();
            stacks = Numbered(model, "stack_");
            insulators = Numbered(model, "insulator_");
            flywheel = PezMotion.FindDeep(model, "flywheel");
            if (flywheel) wheelRest = flywheel.localRotation;
            phase = seed * 0.61f;
        }

        static Transform[] Numbered(Transform model, string prefix)
        {
            var list = new System.Collections.Generic.List<Transform>();
            for (Transform c; (c = PezMotion.FindDeep(model, prefix + list.Count)) != null; ) list.Add(c);
            return list.ToArray();
        }

        public static PowerWorks For(GameObject model, int seed) => new PowerWorks(model.transform, seed);

        /// <summary>The stacks' lips (where steam and arcs leave from).</summary>
        public int Stacks => stacks.Length;
        public Vector3 Mouth(int i) => stacks[i].position;

        /// <summary>Throw an arc when one's due. Call with Fx.Audience set.</summary>
        public void Arcs(float load, bool low)
        {
            if (stacks.Length < 2) return;
            float now = Time.time;
            if (arcNext < 0f) arcNext = now + Random.Range(0.5f, 3f);
            if (now < arcNext) return;
            // A point on each lip's rim, on the side facing the other stack.
            Vector3 a = stacks[0].position, b = stacks[1].position, ab = b - a; ab.y = 0f; ab.Normalize();
            float rim = 0.3f * stacks[0].lossyScale.x;
            var from = a + ab * rim + Random.insideUnitSphere * 0.05f;
            var to = b - ab * rim + Random.insideUnitSphere * 0.05f;
            if (insulators.Length > 0 && Random.value < 0.25f)
            {
                to = insulators[Random.Range(0, insulators.Length)].position;
                if (Random.value < 0.5f) from = b + (to - b).normalized * rim;
            }
            Fx.Arc(from, to, ArcCyan, 0.045f);
            if (low)
            {
                // Stuttering bursts: two or three quick arcs, then a gap.
                if (burst <= 0) burst = Random.Range(2, 4);
                arcNext = --burst > 0 ? now + Random.Range(0.06f, 0.14f) : now + Random.Range(0.8f, 1.8f);
            }
            else arcNext = now + Mathf.Lerp(6.5f, 2f, Mathf.Clamp01(load)) * Random.Range(0.75f, 1.25f);
        }

        public void Tick(float load, bool low, float dt)
        {
            float now = Time.time;
            float goal = low ? 0.7f : Mathf.Lerp(1f, 0.78f, Mathf.Clamp01(load));
            squeeze = squeeze < 0f ? goal : Mathf.MoveTowards(squeeze, goal, dt * 0.55f);
            for (int i = 0; i < coils.Length; i++)
            {
                float h = squeeze;
                if (low) h += (Mathf.Repeat(now * 4f + phase + i * 0.5f, 1f) < 0.5f ? 0.025f : -0.01f); // chatter on the stop
                coils[i].localScale = new Vector3(1f, h, 1f);
            }
            if (flywheel)
            {
                float want = low ? (Mathf.Repeat(now * 0.9f + phase, 1f) < 0.35f ? 150f : 15f) : Mathf.Lerp(40f, 140f, Mathf.Clamp01(load));
                rpm = Mathf.MoveTowards(rpm, want, dt * (low ? 260f : 120f));
                angle = Mathf.Repeat(angle + rpm * 6f * dt, 360f);
                flywheel.localRotation = wheelRest * Quaternion.Euler(angle, 0f, 0f);
            }
        }
    }
}
