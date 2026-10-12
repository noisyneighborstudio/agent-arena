using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The command center's own small motions (MOTION.md command_center); the crane itself is PezMotion's.
    ///  - The comms dish (the model's dish node) turns at 30 deg/s, half that on low power.
    ///  - The amber beacon on the crane's apex (spinner__M_E_Amber) blinks once every 1.5 s.
    ///  - Each time a structure is finished, the flip-top head (the head node, hinged at its back edge) pops open 35 deg in
    ///    0.12 s, holds a moment and drops back shut with a bounce: the dispenser has dispensed.
    /// A model without those nodes gets the parts it has.
    /// </summary>
    public class HqWorks
    {
        readonly Transform dish, head;
        readonly Quaternion dishRest, headRest;
        readonly Renderer beacon;
        readonly float phase;
        float dishYaw, popT = -1f, lit = -1f;

        HqWorks(Transform model, int seed)
        {
            dish = PezMotion.FindDeep(model, "dish");
            head = PezMotion.FindDeep(model, "head");
            if (dish) dishRest = dish.localRotation;
            if (head) headRest = head.localRotation;
            var b = PezMotion.FindDeep(model, "spinner__M_E_Amber");
            beacon = b ? b.GetComponent<Renderer>() : null;
            phase = seed * 0.37f;
            dishYaw = seed * 47f;
        }

        public static HqWorks For(GameObject model, int seed) => new HqWorks(model.transform, seed);

        /// <summary>A structure was finished.</summary>
        public void Pop() => popT = 0f;

        public void Tick(bool lowPower, float dt)
        {
            if (dish)
            {
                dishYaw = Mathf.Repeat(dishYaw + 30f * (lowPower ? 0.5f : 1f) * dt, 360f);
                dish.localRotation = dishRest * Quaternion.Euler(0f, dishYaw, 0f);
            }
            if (head && popT >= 0f)
            {
                popT += dt;
                // Snap open, hold, then fall shut and bounce twice on the hinge.
                float t = popT, a;
                if (t < 0.12f) a = Mathf.SmoothStep(0f, 1f, t / 0.12f);
                else if (t < 0.32f) a = 1f;
                else { float u = t - 0.32f; a = Mathf.Abs(Mathf.Cos(u * 7f)) * Mathf.Exp(-u * 5f); }
                head.localRotation = headRest * Quaternion.Euler(35f * a, 0f, 0f);
                if (t > 1.4f) { head.localRotation = headRest; popT = -1f; }
            }
            if (beacon)
            {
                float k = Mathf.Repeat(Time.time / 1.5f + phase, 1f) < 0.12f ? 1.6f : 0.12f;
                if (k != lit) { PezShade.Set(beacon, k > 1f ? 1f : 0.4f, k); lit = k; } // dark between blinks, not just unlit
            }
        }
    }
}
