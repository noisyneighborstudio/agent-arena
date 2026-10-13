using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The neutral derrick (MOTION.md derrick): a pre-war steel dispenser that pays whoever holds it 1.5 steel/s, with no
    /// power and no ore.
    ///  - Held: the flywheel (dk_wheel) turns, and every 2 s (3 steel a brick, the sim's 1.5/s) a steel brick (dk_brick) is
    ///    pushed out of the mouth under the head's chin, slides down the chute (dk_chute_top to dk_chute_end) and drops into
    ///    the tray (dk_tray) onto the heap of ingots (dk_pile jolts), bounces, and sinks into the heap. The head (dk_head) kicks a few degrees as each brick
    ///    comes out, and the amber pour lamp in the mouth (dk_lamp) lights the pour. The team colour itself never glows.
    ///  - Neutral (sugar-pad grey): still, its lamp dark; now and then grit sifts off its head. Waiting.
    ///  - Captured (or released): the head flips open, wide, and comes down in its new owner's colours with a thud.
    ///  - Under fog: still.
    /// WorldView tints the whole model when the owner changes; this puts the old colours back on the head until the flip
    /// turns it over. A model without these nodes gets the parts it has.
    /// </summary>
    public class DerrickWorks
    {
        const float Beat = 2f;                                  // seconds a brick: 3 steel at the sim's 1.5 steel/s
        const float FlipOpen = 150f;                            // the capture: how far the head flips back, degrees

        readonly GameObject model;
        readonly Transform head, wheel, brick, chuteTop, chuteEnd, tray, pile;
        readonly Vector3 pileRest;
        float pileBob;
        readonly Quaternion headRest, wheelRest, brickRot;
        readonly Renderer lamp;
        readonly float phase;
        float wheelSpeed, wheelAngle, beat, flipT = -1f, kick, nextDust, shownLamp = -1f;
        int teamShown, flipTo;
        bool flipped;

        DerrickWorks(Transform model, int team, int seed)
        {
            this.model = model.gameObject;
            head = PezMotion.FindDeep(model, "dk_head");
            wheel = PezMotion.FindDeep(model, "dk_wheel");
            brick = PezMotion.FindDeep(model, "dk_brick");
            chuteTop = PezMotion.FindDeep(model, "dk_chute_top");
            chuteEnd = PezMotion.FindDeep(model, "dk_chute_end");
            tray = PezMotion.FindDeep(model, "dk_tray");
            pile = PezMotion.FindDeep(model, "dk_pile");
            if (pile) pileRest = pile.localPosition;
            if (head) headRest = head.localRotation;
            if (wheel) wheelRest = wheel.localRotation;
            if (brick) { brickRot = brick.localRotation; brick.gameObject.SetActive(false); }
            var l = PezMotion.FindDeep(model, "dk_lamp");
            var ls = l ? PezShade.Emissive(l.gameObject) : new Renderer[0];
            lamp = ls.Length > 0 ? ls[0] : null;
            teamShown = team;
            phase = seed * 0.37f;
            beat = Mathf.Repeat(seed * 0.29f, 1f) * Beat;        // derricks don't all pour together
            nextDust = Time.time + 2f + Mathf.Repeat(seed * 0.53f, 1f) * 5f;
            Lamp(0f);
        }

        public static DerrickWorks For(GameObject model, int team, int seed) => new DerrickWorks(model.transform, team, seed);

        /// <summary>`sees`: the local view sees its live state. Effects go to Fx.Audience, which the caller sets.</summary>
        public void Tick(Entity e, bool sees, float dt)
        {
            // A new owner: the head flips over into their colours. WorldView has tinted everything already; the head shows
            // the old colours until it's turned over.
            if (e.Team != teamShown)
            {
                if (head) Models.TintTeam(head.gameObject, teamShown);
                flipTo = e.Team; teamShown = e.Team; flipT = 0f; flipped = false;
            }
            bool held = sees && e.Team >= 0 && e.IsComplete;
            float now = Time.time;

            // The flywheel spins up while it's held and coasts down when it isn't.
            wheelSpeed = Mathf.MoveTowards(wheelSpeed, held ? 90f : 0f, dt * (held ? 45f : 20f));
            wheelAngle = Mathf.Repeat(wheelAngle + wheelSpeed * dt, 360f);
            if (wheel) wheel.localRotation = wheelRest * Quaternion.AngleAxis(wheelAngle, Vector3.right);

            // The pour: one brick every Beat seconds while it's held.
            float lampK = 0f;
            if (held)
            {
                float was = beat;
                beat += dt;
                if (beat >= Beat) beat -= Beat;
                if (was < 0.05f && beat >= 0.05f) kick = 1f;     // the brick's pushed out: the head kicks
                lampK = Brick(was / Beat, beat / Beat);
            }
            else if (brick && brick.gameObject.activeSelf) brick.gameObject.SetActive(false);
            kick = Mathf.MoveTowards(kick, 0f, dt / 0.35f);
            // The heap of ingots in the tray jolts as each brick lands on it.
            pileBob = Mathf.MoveTowards(pileBob, 0f, dt / 0.3f);
            if (pile) pile.localPosition = pileRest + Vector3.up * 0.015f * Mathf.Sin(Mathf.PI * pileBob);

            // Neutral: grit sifts off the old head now and then.
            if (sees && e.Team < 0 && e.IsComplete && now >= nextDust)
            {
                if (head) Fx.DecayDust(head.position + Vector3.up * 0.2f, 1.2f);
                nextDust = now + Random.Range(5f, 9f);
            }

            // The head: the capture flip, else the kick of each brick.
            float angle = 4f * Mathf.Sin(Mathf.PI * kick) * kick;
            if (flipT >= 0f)
            {
                flipT += dt;
                float t = flipT;
                if (t < 0.3f) angle = FlipOpen * EaseOut(t / 0.3f);
                else if (t < 0.5f) angle = FlipOpen;
                else if (t < 0.8f) angle = FlipOpen * (1f - EaseIn((t - 0.5f) / 0.3f));
                else { float v = t - 0.8f; angle = 10f * Mathf.Abs(Mathf.Sin(v * 14f)) * Mathf.Exp(-v * 8f); }
                // Turned over at the top of the flip: the new colours.
                if (!flipped && t >= 0.3f) { if (head) Models.TintTeam(head.gameObject, flipTo); flipped = true; }
                if (t >= 0.8f && t - dt < 0.8f && head && sees) Fx.DecayDust(head.position, 1.4f);    // the thud
                if (t > 1.4f) flipT = -1f;
            }
            if (head) head.localRotation = headRest * Quaternion.Euler(angle, 0f, 0f);   // + opens it, as the stems' heads

            Lamp(sees ? lampK : 0.2f);
        }

        /// <summary>One brick's run over a beat (0..1): out of the mouth, down the chute, into the tray, then through the
        /// grate. Returns the pour lamp's light.</summary>
        float Brick(float was, float f)
        {
            if (!brick || !chuteTop || !chuteEnd || !tray) return 0f;
            var p = brick.parent;
            Vector3 a = p.InverseTransformPoint(chuteTop.position), b = p.InverseTransformPoint(chuteEnd.position), c = p.InverseTransformPoint(tray.position);
            Vector3 pos; float scale = 1f, spin = 0f;
            bool show = true;
            if (f < 0.12f) { pos = a + (a - b).normalized * 0.12f * (1f - f / 0.12f); scale = Mathf.Lerp(0.4f, 1f, f / 0.12f); }   // pushed out of the mouth
            else if (f < 0.42f) { float k = (f - 0.12f) / 0.3f; pos = Vector3.Lerp(a, b, k * k); }                       // slides down, gathering speed
            else if (f < 0.56f)
            {
                // Off the lip and into the tray, with a bounce.
                float k = (f - 0.42f) / 0.14f;
                pos = Vector3.Lerp(b, c, k) + Vector3.up * (0.08f * Mathf.Sin(Mathf.PI * k));
                spin = 25f * k;
            }
            else if (f < 0.7f) { float k = (f - 0.56f) / 0.14f; pos = c + Vector3.up * 0.03f * Mathf.Abs(Mathf.Sin(k * Mathf.PI * 2f)) * (1f - k); spin = 25f; }
            else if (f < 0.85f) { float k = (f - 0.7f) / 0.15f; pos = c + Vector3.down * 0.09f * k; spin = 25f; scale = 1f - 0.3f * k; }   // through the grate
            else { pos = c; show = false; }
            if (brick.gameObject.activeSelf != show) brick.gameObject.SetActive(show);
            if (show)
            {
                brick.localPosition = pos;
                brick.localRotation = Quaternion.AngleAxis(spin, Vector3.up) * brickRot;
                brick.localScale = Vector3.one * scale;
            }
            if (was < 0.56f && f >= 0.56f) { Fx.PourDust(tray.position, PezPalette.MaterialsSpringSteel); pileBob = 1f; }   // it lands: a puff of steel dust
            // The pour lamp: up as the brick comes out, out once it's in the tray.
            return f < 0.05f ? f / 0.05f : f < 0.5f ? 1f : f < 0.62f ? 1f - (f - 0.5f) / 0.12f : 0f;
        }

        static float EaseOut(float k) => 1f - (1f - k) * (1f - k) * (1f - k);
        static float EaseIn(float k) => k * k * k;

        void Lamp(float g)
        {
            if (Mathf.Abs(g - shownLamp) < 0.005f) return;
            shownLamp = g;
            if (lamp) PezShade.Set(lamp, Mathf.Lerp(0.25f, 1f, g), g);
        }
    }
}
