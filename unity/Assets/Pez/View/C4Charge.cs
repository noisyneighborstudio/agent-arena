using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// A commando's C4 (MOTION.md commando): a copy of the candy charge from his hand, lobbed onto the target, stuck there
    /// and blinking until the sim's `shot` blows it (WorldView asks <see cref="SoldierWorks.Detonate"/>, which removes it
    /// where the blast goes off). It's the charge's own motion, so it lives on the charge: it outlives the throw, rides
    /// on a moving target, and if nobody feeds its fuse any more (the commando died, walked off or lost his target) it
    /// fizzles out by itself.
    ///  - flight: from the hand to the stick point in an arc, tumbling end over end;
    ///  - landing: it splats flat against the surface (squashes along the normal and springs back in 0.15 s);
    ///  - stuck: its amber lamp blinks faster and faster, up to ten times a second, as the fuse (the sim's arming left or
    ///    its weapon cooldown, whichever is longer, fed by SoldierWorks) runs down.
    /// </summary>
    public class C4Charge : MonoBehaviour
    {
        const float Flight = 0.42f, Arc = 0.32f, Splat = 0.15f, Starve = 1.2f;

        Transform host;
        Vector3 from, at, size;          // at: host-local; size: the charge's world scale
        Quaternion fromRot, stuckLocal;
        float t0, fed, fuse = 1f, phase, fizzle = -1f;
        Renderer lamp;
        bool lit;
        public int Target { get; private set; }

        /// <param name="hand">The charge in the commando's hand: copied, at its world pose.</param>
        /// <param name="host">What it sticks to (the target's root): it rides along with it, and goes when it goes.</param>
        /// <param name="stick">Where it sticks (world).</param>
        /// <param name="outward">The surface's outward normal there.</param>
        public static C4Charge Throw(Transform hand, Transform host, int target, Vector3 stick, Vector3 outward)
        {
            var go = Instantiate(hand.gameObject);
            go.name = "c4";
            var t = go.transform;
            t.SetParent(host, false);
            t.position = hand.position;
            t.rotation = hand.rotation;
            var c = go.AddComponent<C4Charge>();
            c.host = host;
            c.Target = target;
            c.size = hand.lossyScale;
            t.localScale = Vector3.one;
            c.size = new Vector3(c.size.x / host.lossyScale.x, c.size.y / host.lossyScale.y, c.size.z / host.lossyScale.z);
            t.localScale = c.size;
            c.from = host.InverseTransformPoint(hand.position);
            c.fromRot = Quaternion.Inverse(host.rotation) * hand.rotation;
            c.at = host.InverseTransformPoint(stick);
            // Stuck flat on the surface, its face (local -x, the lamp's side) out along the normal, its cap (local y) as
            // near to up as the surface allows (on a roof: pointing on, away from the thrower).
            if (outward.sqrMagnitude < 1e-6f) outward = -hand.forward;
            var x = -outward.normalized;
            var away = stick - hand.position; away.y = 0f;
            var y = Vector3.ProjectOnPlane(Mathf.Abs(x.y) > 0.9f ? away : Vector3.up, x);
            if (y.sqrMagnitude < 1e-6f) y = Vector3.ProjectOnPlane(Vector3.forward, x);
            y.Normalize();
            var worldRot = Quaternion.LookRotation(Vector3.Cross(x, y), y);
            c.stuckLocal = Quaternion.Inverse(host.rotation) * worldRot;
            c.t0 = Time.time;
            c.fed = Time.time;
            // The lamp on its face: glow means state (armed), so only the planted charge has one.
            var l = Models.Part(t, PrimitiveType.Sphere, new Vector3(-0.032f, 0.006f, 0f), Vector3.one * 0.046f, Mats.Glow(PezPalette.EmissiveAmberIndustryDocking, 4f));
            c.lamp = l.GetComponent<Renderer>();
            c.lamp.enabled = false;
            foreach (var tr in go.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = host.gameObject.layer;
            return c;
        }

        /// <summary>The fuse left, 1 just armed .. 0 about to blow (the time to the bang over the arming or cooldown it's in). Call every frame.</summary>
        public void Feed(float left) { fuse = Mathf.Clamp01(left); fed = Time.time; }

        /// <summary>Where it is now (world): the blast goes off here.</summary>
        public Vector3 Position => transform.position;

        /// <summary>Gone without a bang (its commando stopped feeding it).</summary>
        public void Fizzle() { if (fizzle < 0f) fizzle = Time.time; }

        void Update()
        {
            if (host == null) { Destroy(gameObject); return; }
            float now = Time.time, u = (now - t0) / Flight;
            var t = transform;
            if (u < 1f)
            {
                // The lob: an arc from the hand to the wall, tumbling end over end (one and a half turns).
                float e = u;
                var p = Vector3.Lerp(from, at, e);
                t.localPosition = p + host.InverseTransformVector(Vector3.up) * (4f * Arc * e * (1f - e));
                t.localRotation = Quaternion.Slerp(fromRot, stuckLocal, e) * Quaternion.Euler(540f * e, 0f, 0f);
                t.localScale = size;
                return;
            }
            t.localPosition = at;
            t.localRotation = stuckLocal;
            // The splat: squashed flat along the surface's normal (local x), springing back.
            float s = Mathf.Clamp01((now - t0 - Flight) / Splat);
            float squash = 1f - 0.45f * (1f - s) * (1f - s);
            float k = 1f;
            if (fizzle < 0f && now - fed > Starve) fizzle = now;
            if (fizzle >= 0f)
            {
                k = 1f - Mathf.Clamp01((now - fizzle) / 0.35f);
                if (k <= 0f) { Destroy(gameObject); return; }
            }
            t.localScale = Vector3.Scale(size, new Vector3(squash, 2f - squash, 2f - squash)) * k;
            // The blink: about 1 Hz when just armed, 10 Hz at the end; each flash on for 45% of its beat. Out when fizzling.
            float period = Mathf.Lerp(0.1f, 0.9f, fuse);
            phase += Time.deltaTime / period;
            bool on = fizzle < 0f && s >= 1f && phase % 1f < 0.45f;
            if (on != lit) { lamp.enabled = on; lit = on; }
        }
    }
}
