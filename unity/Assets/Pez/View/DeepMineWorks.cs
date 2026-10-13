using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The deep mine's headframe (MOTION.md deep_mine), run from the sim. It also draws the derrick (a derrick wears the
    /// deep mine's model), which has no deposit and needs no power: its stem stays full, its ore is iron (it pays steel),
    /// and its hoist runs while someone holds it.
    ///  - The hoist: while the mine works (the sim's Working: the deposit has ore and it's built) the skip (dm_skip) hauls
    ///    a load of ore candy (dm_load) up out of the shaft on its rope, tips its bucket (dm_bucket) at the top so the ore
    ///    pours down the chute into the bin (dm_heap bobs as it lands and a puff of ore dust rises), and goes back down
    ///    for the next. The ventilation fan (dm_fan) turns while it works and coasts down when it stops. One trip every
    ///    4 s (16 ore at the sim's 4/s); on low power, as the sim halves the rate, every 8 s, and the winder labours, the
    ///    skip slowing to a crawl now and then on the way up. The winding drum (dm_drum) and the sheave (dm_sheave) turn
    ///    with the rope, so they speed up, slow down and reverse with the skip; the rope below the sheave (dm_rope) stretches
    ///    to the skip's eye (dm_eye). When the mine stops, the trip in hand finishes and the skip parks in the shaft.
    ///  - The stem shows what's left: six ore bricks (dm_res_0..5, top down), one per sixth of the deposit, on a spring
    ///    (dm_spring, dm_follower). Each time the deposit drops below a sixth the team flip-top head (dm_head) pops, the top
    ///    brick (dm_candy) flips out and the spring pushes the rest up a slot. Dry, the stem is empty and the head hangs
    ///    open; the bin's heap drains out of sight.
    ///  - Glow is state: the amber lamp (dm_lamp) is dark while the deposit holds up, pulses once it runs low (the sim's
    ///    running-low warning: 5 minutes left) and blinks hard when it's dry. Dark until built; under fog, neutral and still.
    /// The ore parts take the deposit's colour (Models.TintOre). It owns the lamp: For() takes it out of WorldView's
    /// generic power glow. A model without these nodes gets the parts it has.
    /// </summary>
    public class DeepMineWorks
    {
        const int Bricks = 6;
        const float Trip = 4f;                                  // seconds per trip at full rate
        const float Load = 0.1f, Up = 0.45f, TipOut = 0.51f, Pour = 0.56f, TipBack = 0.62f, Down = 0.95f; // a trip, 0..1
        const float Rise = 1.1f;                                // the skip's travel, model units (rest -0.1 to the dump at 1.0)
        const float Tip = 58f;                                  // the bucket's tip at the dump, degrees
        const float SheaveR = 0.262f, DrumR = 0.128f;           // the rope's radius on the sheave and on the drum (build_models.py)
        const float HangOpen = 34f, PopOpen = 48f;              // the head: hanging open on an empty stem; popped

        readonly GameObject model;
        readonly Transform fan;
        readonly Quaternion fanRest;
        float fanSpeed, fanAngle;
        readonly Transform skip, bucket, load, eye, rope, sheave, drum, heap, head, candy, spring, follower, bin;
        readonly Transform[] res;
        readonly Vector3 skipRest, heapRest, followerRest, springRest, springScale, candyRest, tipDir;
        readonly Quaternion bucketRest, sheaveRest, drumRest, headRest, candyRot;
        readonly Vector3[] slots;
        readonly Vector3 sheaveAxis, drumAxis;
        readonly float sheaveSign, drumSign, pitch, springSpan, phase;
        readonly Renderer lamp;
        float u, lastS, sheaveAngle, drumAngle, bob, drain, headAngle, popT = -1f, shownLamp = -1f;
        int shown = -1, ore = -1;
        bool running, adding, started;
        readonly float offset;

        DeepMineWorks(Transform model, int seed, ref Renderer[] glows)
        {
            this.model = model.gameObject;
            skip = PezMotion.FindDeep(model, "dm_skip");
            bucket = PezMotion.FindDeep(model, "dm_bucket");
            load = PezMotion.FindDeep(model, "dm_load");
            eye = PezMotion.FindDeep(model, "dm_eye");
            rope = PezMotion.FindDeep(model, "dm_rope");
            sheave = PezMotion.FindDeep(model, "dm_sheave");
            drum = PezMotion.FindDeep(model, "dm_drum");
            heap = PezMotion.FindDeep(model, "dm_heap");
            head = PezMotion.FindDeep(model, "dm_head");
            candy = PezMotion.FindDeep(model, "dm_candy");
            spring = PezMotion.FindDeep(model, "dm_spring");
            follower = PezMotion.FindDeep(model, "dm_follower");
            bin = PezMotion.FindDeep(model, "dm_bin");
            var list = new System.Collections.Generic.List<Transform>();
            for (Transform c; list.Count < Bricks && (c = PezMotion.FindDeep(model, "dm_res_" + list.Count)) != null; ) list.Add(c);
            res = list.ToArray();
            slots = new Vector3[res.Length];
            for (int i = 0; i < res.Length; i++) slots[i] = res[i].localPosition;
            pitch = res.Length > 1 ? slots[0].y - slots[1].y : 0.095f;

            if (skip) skipRest = skip.localPosition;
            if (bucket) bucketRest = bucket.localRotation;
            if (heap) heapRest = heap.localPosition;
            if (head) headRest = head.localRotation;
            if (follower) followerRest = follower.localPosition;
            if (spring) { springRest = spring.localPosition; springScale = spring.localScale; }
            if (follower && spring) springSpan = Mathf.Max(0.01f, followerRest.y - 0.02f - springRest.y);
            if (candy) { candyRest = candy.localPosition; candyRot = candy.localRotation; candy.gameObject.SetActive(false); }

            // Directions from the model itself (its x is mirrored on import). The bucket tips toward the bin, in the skip's
            // space; the sheave turns so the rope's shaft side climbs as the skip rises; the drum so its top runs away from
            // the sheave, winding the rope on.
            if (bucket && bin)
            {
                var d = skip.InverseTransformPoint(bin.position) - bucket.localPosition; d.y = 0f;
                tipDir = d.sqrMagnitude > 1e-6f ? d.normalized : Vector3.right;
            }
            if (sheave)
            {
                sheaveRest = sheave.localRotation;
                var off = rope ? sheave.InverseTransformPoint(rope.position) : Vector3.right;
                var h = new Vector3(off.x, 0f, off.z);
                sheaveAxis = h.sqrMagnitude > 1e-6f ? Vector3.Cross(h, Vector3.up).normalized : Vector3.forward;
                sheaveSign = (Quaternion.AngleAxis(10f, sheaveAxis) * h).y > 0f ? 1f : -1f;
            }
            if (drum)
            {
                drumRest = drum.localRotation;
                var away = sheave ? drum.InverseTransformPoint(2f * drum.position - sheave.position) : Vector3.left;
                away.y = 0f;
                drumAxis = away.sqrMagnitude > 1e-6f ? Vector3.Cross(away, Vector3.up).normalized : Vector3.forward;
                drumSign = Vector3.Dot(Quaternion.AngleAxis(10f, drumAxis) * Vector3.up, away) > 0f ? 1f : -1f;
            }
            fan = PezMotion.FindDeep(model, "dm_fan");
            if (fan) fanRest = fan.localRotation;
            var l = PezMotion.FindDeep(model, "dm_lamp");
            var ls = l ? PezShade.Emissive(l.gameObject) : new Renderer[0];
            lamp = ls.Length > 0 ? ls[0] : null;
            if (glows != null && lamp) glows = System.Array.FindAll(glows, r => r != lamp);
            phase = seed * 0.37f;
            offset = Mathf.Repeat(seed * 0.29f, 1f);             // mines don't all hoist together
            Pose(0f, 0f);
        }

        public static DeepMineWorks For(GameObject model, int seed, ref Renderer[] glows) => new DeepMineWorks(model.transform, seed, ref glows);

        /// <summary>
        /// `sees`: the local view sees its live state (else it holds still, its lamp neutral). Effects go to Fx.Audience,
        /// which the caller sets.
        /// </summary>
        public void Tick(World world, Entity e, bool sees, float dt)
        {
            if (!sees) { Lamp(0.25f); return; } // remembered under fog: no live state
            bool complete = e.IsComplete;
            bool derrick = e.Def.Key != "deep_mine";
            var deposit = derrick ? null : world.Map.DepositById(e.DepositId);
            // A derrick pays steel: its ore is iron. A deep mine's is its deposit's.
            int type = deposit != null ? deposit.Type : derrick ? 0 : -1;
            if (type >= 0 && type != ore) { Models.TintOre(model, type); ore = type; }
            bool low = !derrick && e.Team >= 0 && world.Teams[e.Team].LowPower;   // derricks need no power
            bool working = complete && e.Team >= 0 && e.Working;
            float frac = deposit == null ? 1f : deposit.Initial > 0f ? deposit.Amount / deposit.Initial : 0f;
            bool dry = deposit != null && deposit.Amount <= 0f;
            bool runningLow = deposit != null && !dry && deposit.Amount <= World.DeepMineWarnAmount;
            float now = Time.time;

            // The hoist: runs while working; stopped, it finishes the trip in hand and parks at the bottom. A mine already at
            // work when first seen starts partway through a trip; anything still being built waits at the bottom.
            if (!started && complete) { started = true; if (working) u = offset; }
            if (!complete) { u = 0f; running = false; }
            if (working) running = true;
            if (running && complete)
            {
                float rate = (low ? 0.5f : 1f) / Trip;
                // On low power the winder labours: twice a trip, for a moment, the skip slows to a crawl on its way up.
                if (low && u > Load && u < Up && Mathf.Repeat(now / 2.7f + phase, 1f) < 0.16f) rate *= 0.2f;
                float was = u;
                u += dt * rate;
                if (was < TipOut && u >= TipOut) Spill();
                if (u >= 1f) { u -= 1f; if (!working) { running = false; u = 0f; } }
            }
            float s = SkipAt(u);
            Pose(s, TipAt(u));
            // The drum and the sheave turn with the rope.
            float ds = s - lastS; lastS = s;
            sheaveAngle = Mathf.Repeat(sheaveAngle + sheaveSign * ds / SheaveR * Mathf.Rad2Deg, 360f);
            drumAngle = Mathf.Repeat(drumAngle + drumSign * ds / DrumR * Mathf.Rad2Deg, 360f);
            if (sheave) sheave.localRotation = sheaveRest * Quaternion.AngleAxis(sheaveAngle, sheaveAxis);
            if (drum) drum.localRotation = drumRest * Quaternion.AngleAxis(drumAngle, drumAxis);

            // The ventilation fan runs while the mine works (at half speed on low power) and coasts down when it stops.
            fanSpeed = Mathf.MoveTowards(fanSpeed, working ? (low ? 270f : 540f) : 0f, dt * (working ? 360f : 180f));
            fanAngle = Mathf.Repeat(fanAngle + fanSpeed * dt, 360f);
            if (fan) fan.localRotation = fanRest * Quaternion.AngleAxis(fanAngle, Vector3.forward);

            // The bin: the heap bobs as each load lands; dry, it drains away.
            bob = Mathf.MoveTowards(bob, 0f, dt / 0.5f);
            drain = Mathf.MoveTowards(drain, complete && dry && !running ? 1f : 0f, dt / (dry ? 1.5f : 0.8f));
            if (heap) heap.localPosition = heapRest + Vector3.up * (0.02f * Mathf.Sin(Mathf.PI * bob)) + Vector3.down * 0.3f * drain;

            // The stem: one brick per sixth of what's left.
            int want = frac <= 0f ? 0 : Mathf.Clamp(Mathf.CeilToInt(frac * Bricks - 0.001f), 1, Bricks);
            if (shown < 0) shown = want;                       // first sight: as it is, no pops
            if (popT < 0f && want != shown) { popT = 0f; adding = want > shown; }
            Stem(dt);

            // Glow is state.
            if (!complete) Lamp(0.05f);
            else if (dry) Lamp(Mathf.Repeat(now * 0.9f + phase, 1f) < 0.6f ? 1f : 0.15f);
            else if (runningLow) Lamp(Mathf.Lerp(0.15f, low ? 0.5f : 0.9f, 0.5f + 0.5f * Mathf.Sin((now * 0.5f + phase) * Mathf.PI * 2f)));
            else Lamp(0.08f);
        }

        /// <summary>The skip's height above its rest over a trip.</summary>
        static float SkipAt(float u)
        {
            if (u < Load) return 0f;
            if (u < Up) return Rise * Mathf.SmoothStep(0f, 1f, (u - Load) / (Up - Load));
            if (u < TipBack) return Rise;
            if (u < Down) return Rise * (1f - Mathf.SmoothStep(0f, 1f, (u - TipBack) / (Down - TipBack)));
            return 0f;
        }

        /// <summary>The bucket's tip (0..1) over a trip.</summary>
        static float TipAt(float u)
        {
            if (u < Up || u >= TipBack) return 0f;
            if (u < TipOut) return Mathf.SmoothStep(0f, 1f, (u - Up) / (TipOut - Up));
            if (u < Pour) return 1f;
            return 1f - Mathf.SmoothStep(0f, 1f, (u - Pour) / (TipBack - Pour));
        }

        void Pose(float s, float tip)
        {
            if (skip) skip.localPosition = skipRest + Vector3.up * s;
            if (bucket) bucket.localRotation = Quaternion.FromToRotation(Vector3.up, Vector3.up * Mathf.Cos(tip * Tip * Mathf.Deg2Rad) + tipDir * Mathf.Sin(tip * Tip * Mathf.Deg2Rad)) * bucketRest;
            // The load rides up full and pours out as the bucket tips; it's back by the time the skip's down the shaft.
            if (load)
            {
                float k = u < TipOut ? 1f : u < Pour + 0.02f ? 1f - (u - TipOut) / (Pour + 0.02f - TipOut) : u < Down ? 0f : 1f;
                bool show = k > 0.02f;
                if (load.gameObject.activeSelf != show) load.gameObject.SetActive(show);
                if (show) load.localScale = Vector3.one * Mathf.Lerp(0.4f, 1f, k);
            }
            // The rope below the sheave reaches down to the skip's eye (its mesh is 1 long).
            if (rope && eye)
            {
                float len = (rope.position.y - eye.position.y) / Mathf.Max(1e-4f, rope.parent.lossyScale.y);
                rope.localScale = new Vector3(1f, Mathf.Max(0.001f, len), 1f);
            }
        }

        /// <summary>The ore pours out of the tipped bucket into the bin: the heap bobs and a puff of ore dust rises.</summary>
        void Spill()
        {
            bob = 1f;
            if (!bin) return;
            Color32 c = WorldView.OreColors[Mathf.Clamp(ore, 0, WorldView.OreColors.Length - 1)];
            Fx.PourDust(bin.position, c);
        }

        /// <summary>
        /// The stem: `shown` bricks stacked from the top on the spring. A pop takes the top brick out (the head snaps open, the
        /// brick flips out over the lip, the stack rides up a slot, the head drops shut with a bounce); a refill (only ever in
        /// the kitchen sink) pushes one in from the top, the stack giving a slot. On an empty stem the head hangs open.
        /// </summary>
        void Stem(float dt)
        {
            int n = shown;
            float first = 0f;      // the slot the top brick sits in (0 = the top slot, + is down)
            if (popT >= 0f)
            {
                popT += dt;
                float t = popT;
                float from = shown == 0 ? HangOpen / PopOpen : 0f;
                float to = (adding ? shown + 1 : shown - 1) == 0 ? HangOpen / PopOpen : 0f;
                float a = from + (1f - from) * Mathf.SmoothStep(0f, 1f, t / 0.1f);
                if (t >= 0.6f) { float v = t - 0.6f; a = to + (1f - to) * Mathf.Abs(Mathf.Cos(v * 7f)) * Mathf.Exp(-v * 5f); }
                headAngle = PopOpen * a;
                float k = Mathf.SmoothStep(0f, 1f, (t - 0.12f) / 0.28f);
                if (!adding && t >= 0.1f) { n = shown - 1; first = 1f - k; }   // the top brick is the candy now; the rest ride up
                else if (adding) { n = shown + 1; first = k - 1f; }             // a new one comes in from the head, pushing down
                if (candy)
                {
                    float f = adding ? 0f : (t - 0.1f) / 0.8f;
                    bool fly = f > 0f && f < 1f;
                    if (candy.gameObject.activeSelf != fly) candy.gameObject.SetActive(fly);
                    if (fly)
                    {
                        // From the top slot, up out of the mouth and over the front lip (away from the hinge), and away.
                        var top = res.Length > 0 ? candy.parent.InverseTransformPoint(res[0].parent.TransformPoint(slots[0])) : candyRest;
                        var outDir = head ? candyRest - candy.parent.InverseTransformPoint(head.position) : Vector3.back;
                        outDir.y = 0f; outDir = outDir.sqrMagnitude > 1e-6f ? outDir.normalized : Vector3.back;
                        // Straight up clear of the stem's lip first, then out under the open head before it drops.
                        float up = Mathf.SmoothStep(0f, 1f, f / 0.3f) * 0.26f;
                        float fwd = Mathf.Max(0f, f - 0.3f) * 0.8f;
                        float fall = f > 0.55f ? (f - 0.55f) * (f - 0.55f) * 2.4f : 0f;
                        candy.localPosition = top + Vector3.up * (up - fall) + outDir * fwd;
                        candy.localRotation = candyRot * Quaternion.AngleAxis(-300f * Mathf.Max(0f, f - 0.35f), Vector3.Cross(Vector3.up, outDir));
                        candy.localScale = Vector3.one * (f < 0.7f ? 1f : Mathf.Lerp(1f, 0f, (f - 0.7f) / 0.3f));
                    }
                }
                if (t > 1.55f)
                {
                    shown += adding ? 1 : -1;
                    popT = -1f; n = shown; first = 0f;
                    headAngle = shown == 0 ? HangOpen : 0f;
                    if (candy && candy.gameObject.activeSelf) candy.gameObject.SetActive(false);
                }
            }
            else headAngle = Mathf.MoveTowards(headAngle, shown == 0 ? HangOpen : 0f, dt * 120f);
            if (head) head.localRotation = headRest * Quaternion.Euler(headAngle, 0f, 0f);

            for (int i = 0; i < res.Length; i++)
            {
                bool show = i < n;
                if (res[i].gameObject.activeSelf != show) res[i].gameObject.SetActive(show);
                if (show) res[i].localPosition = slots[0] + Vector3.down * pitch * (first + i);
            }
            // The follower sits under the lowest brick (on an empty stem, up under the head); the spring reaches up to it.
            if (follower)
            {
                float bottom = n > 0 ? first + n - 1 : -1f;
                float y = followerRest.y + pitch * (Bricks - 1 - bottom);
                follower.localPosition = new Vector3(followerRest.x, y, followerRest.z);
                if (spring) spring.localScale = new Vector3(springScale.x, springScale.y * (y - 0.02f - springRest.y) / springSpan, springScale.z);
            }
        }

        void Lamp(float g)
        {
            if (Mathf.Abs(g - shownLamp) < 0.005f) return;
            shownLamp = g;
            // The lamp body darkens with its light, as WorldView's power glow does.
            if (lamp) PezShade.Set(lamp, Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.25f, 1f, g)), g);
        }
    }
}
