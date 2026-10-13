using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// A deep mine (MOTION.md deep_mine_iron, _copper, _crystal, _uranium and the fallback deep_mine), run from the sim. Every
    /// mine is one family: the same reserve stem and lamp, and one trip of its machine every 4 s while it works (16 ore at the
    /// sim's 4/s; every 8 s on low power, which halves the rate). What the machine is depends on the nodes its model has:
    ///  - A hoist (iron's pithead, crystal's core drill, the fallback headframe): the skip (dm_skip) hauls a load (dm_load)
    ///    up to the top (dm_top, or 1.1 up), its bucket (dm_bucket) tips toward the bin (dm_bin) so the load pours in
    ///    (dm_heap bobs), and it goes back down. The sheave (dm_sheave) and the drum (dm_drum) turn with the rope; the rope
    ///    below the sheave (dm_rope) stretches to the skip's eye (dm_eye). Iron's bricks (dm_tumble_N) tumble down the chute
    ///    (from dm_chute) into the hopper;
    ///    crystal's core glows cyan only while it works and its shards chime as they land; its drill string (dm_drill) turns.
    ///  - A belt (copper's incline): bricks (dm_carry_N) ride from the tail (dm_belt_a) to the head (dm_belt_b), one a
    ///    second, and drop into the crusher (dm_crusher turns; a puff of mint dust); the pulleys (dm_pulley_N) turn with the
    ///    belt. Stopped (dry), the bricks on it shrink away where they are in 0.5 s and the belt coasts to a stop: no ore
    ///    arrives after the sim says the deposit has none.
    ///  - A jib (uranium's containment): the hook (dm_skip, on dm_jib) lifts an acid rod (dm_load) out of the dome's port to
    ///    dm_top, the jib swings it over the cask (dm_bin: where, and how low), lowers it in, and swings back; the carousel
    ///    (dm_carousel) turns the next cask round. The rods and the sight glasses (dm_glow) are lit while there's uranium.
    ///  - The stem shows what's left: six ore pieces (dm_res_0..5, top down), one per sixth of the deposit, on a spring
    ///    (dm_spring, dm_follower). Each time the deposit drops below a sixth the team flip-top head (dm_head) pops, the top
    ///    piece (dm_candy) flips out and the spring pushes the rest up a slot. Dry, the stem is empty and the head hangs open.
    ///  - Glow is state: the amber lamp (dm_lamp) is dark while the deposit holds up and pulses once it runs low (the sim's
    ///    running-low warning: 5 minutes left). When the deposit runs dry the trip in hand finishes empty, a last puff of dust rises
    ///    from the shaft, the lamp blinks three times and goes out, and the whole mine darkens to 60% over 2 s: it stands
    ///    dark and still, finished, readable from across the map. Low power: half
    ///    speed, and the mine's own glows at half. Dark until built; under fog, neutral and still.
    /// The ore parts take the deposit's colour (Models.TintOre). It owns the glows it drives: For() takes them out of
    /// WorldView's generic power glow. A model without these nodes gets the parts it has.
    /// </summary>
    public class DeepMineWorks
    {
        enum Kind { Hoist, Belt, Jib }

        const int Bricks = 6;
        const float Trip = 4f;                                  // seconds per trip at full rate
        // A hoist's trip, 0..1: load, up, tip out, pour, tip back, down.
        const float Load = 0.1f, Up = 0.45f, TipOut = 0.51f, Pour = 0.56f, TipBack = 0.62f, Down = 0.95f;
        // A jib's trip, 0..1: up, swing out, lower, release, raise, swing back, lower into the port.
        const float JUp0 = 0.06f, JUp1 = 0.22f, JOut1 = 0.36f, JLow1 = 0.5f, JRel = 0.54f, JRaise1 = 0.66f, JBack1 = 0.8f, JHome1 = 0.94f;
        const float Tip = 58f;                                  // the bucket's tip at the dump, degrees
        const float HangOpen = 34f, PopOpen = 48f;              // the head: hanging open on an empty stem; popped
        const float BeltBricksPerSecond = 1f;                   // copper: one brick into the crusher a second (4 ore each)

        readonly Kind kind;
        readonly GameObject model;
        readonly Transform fan, drill, crusher, jib, carousel;
        readonly Quaternion fanRest, drillRest, crusherRest, jibRest, carouselRest;
        float fanSpeed, fanAngle, drillSpeed, drillAngle, crusherAngle, carouselAngle, carouselFrom, carouselT = -1f;
        readonly Transform skip, bucket, load, eye, rope, sheave, drum, heap, head, candy, spring, follower, bin, chute;
        readonly Transform[] res, tumble;
        float tumbleT = -1f;
        readonly Vector3 skipRest, heapRest, followerRest, springRest, springScale, candyRest, tipDir;
        readonly Quaternion bucketRest, sheaveRest, drumRest, headRest, candyRot;
        readonly Vector3[] slots;
        readonly Vector3 sheaveAxis, drumAxis;
        readonly float sheaveSign, drumSign, sheaveR, drumR, pitch, springSpan, phase, rise, lower, swing;
        // The belt.
        readonly Transform beltA, beltB;
        readonly Transform[] carry, pulleys;
        readonly Quaternion[] pulleyRest;
        readonly bool[] carried;
        readonly float[] pulleyR;
        readonly float beltLen;
        float beltS, beltSpeed, pulleyAngle;
        // Glows it drives itself.
        readonly Renderer lamp;
        readonly Renderer[] core, ore, all;
        float dead, shownDead = -1f;
        float u, lastS, sheaveAngle, drumAngle, bob, drain, headAngle, popT = -1f, shownLamp = -1f, shownCore = -1f, shownOre = -1f, dryT = -1f;
        int shown = -1, oreType = -1;
        bool running, adding, started, wasDry, empty;
        float beltFade = 1f;
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
            chute = PezMotion.FindDeep(model, "dm_chute");
            jib = PezMotion.FindDeep(model, "dm_jib");
            beltA = PezMotion.FindDeep(model, "dm_belt_a");
            beltB = PezMotion.FindDeep(model, "dm_belt_b");
            kind = beltA && beltB ? Kind.Belt : jib && skip ? Kind.Jib : Kind.Hoist;
            res = Numbered(model, "dm_res_", Bricks);
            tumble = Numbered(model, "dm_tumble_", 6);
            foreach (var t in tumble) t.gameObject.SetActive(false);
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

            // The skip's travel: up to dm_top (a marker beside it) if the model has one.
            var top = PezMotion.FindDeep(model, "dm_top");
            rise = skip && top ? Mathf.Max(0.05f, skip.parent.InverseTransformPoint(top.position).y - skipRest.y) : 1.1f;

            // Directions and sizes from the model itself (its x is mirrored on import). The bucket tips toward the bin, in
            // the skip's space; the sheave turns so the rope's shaft side climbs as the skip rises; the drum so its top runs
            // away from the sheave, winding the rope on. The rope's radius on each is where the rope leaves it.
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
                sheaveR = rope ? Mathf.Max(0.02f, h.magnitude) : 0.262f;     // the rope's radius on it
                sheaveAxis = h.sqrMagnitude > 1e-6f ? Vector3.Cross(h, Vector3.up).normalized : Vector3.forward;
                sheaveSign = (Quaternion.AngleAxis(10f, sheaveAxis) * h).y > 0f ? 1f : -1f;
            }
            if (drum)
            {
                drumRest = drum.localRotation;
                drumR = 0.128f;
                var away = sheave ? drum.InverseTransformPoint(2f * drum.position - sheave.position) : Vector3.left;
                away.y = 0f;
                drumAxis = away.sqrMagnitude > 1e-6f ? Vector3.Cross(away, Vector3.up).normalized : Vector3.forward;
                drumSign = Vector3.Dot(Quaternion.AngleAxis(10f, drumAxis) * Vector3.up, away) > 0f ? 1f : -1f;
            }
            fan = PezMotion.FindDeep(model, "dm_fan");
            if (fan) fanRest = fan.localRotation;
            drill = PezMotion.FindDeep(model, "dm_drill");
            if (drill) drillRest = drill.localRotation;
            crusher = PezMotion.FindDeep(model, "dm_crusher");
            if (crusher) crusherRest = crusher.localRotation;
            carousel = PezMotion.FindDeep(model, "dm_carousel");
            if (carousel) carouselRest = carousel.localRotation;
            if (jib)
            {
                jibRest = jib.localRotation;
                // The swing, about the jib's own up: from the hook's bearing to the cask's.
                if (bin && skip)
                {
                    var hook = jib.InverseTransformPoint(skip.position); hook.y = 0f;
                    var cask = jib.InverseTransformPoint(bin.position); cask.y = 0f;
                    swing = Vector3.SignedAngle(hook, cask, Vector3.up);
                    lower = Mathf.Max(0.05f, skipRest.y + rise - skip.parent.InverseTransformPoint(bin.position).y);
                }
            }

            if (kind == Kind.Belt)
            {
                carry = Numbered(model, "dm_carry_", 16);
                carried = new bool[carry.Length];
                for (int i = 0; i < carried.Length; i++) carried[i] = true;
                beltLen = Vector3.Distance(beltA.localPosition, beltB.localPosition);
            }
            pulleys = Numbered(model, "dm_pulley_", 4);
            pulleyRest = new Quaternion[pulleys.Length];
            pulleyR = new float[pulleys.Length];
            for (int i = 0; i < pulleys.Length; i++) { pulleyRest[i] = pulleys[i].localRotation; pulleyR[i] = 0.05f; }

            // Its own glows: the lamp; the core that glows only while it works (crystal); the ore's own light (uranium's rods
            // and sight glasses), lit while there's ore.
            var l = PezMotion.FindDeep(model, "dm_lamp");
            var ls = l ? PezShade.Emissive(l.gameObject) : new Renderer[0];
            lamp = ls.Length > 0 ? ls[0] : null;
            var loadGlow = load ? PezShade.Emissive(load.gameObject) : new Renderer[0];
            var g = PezMotion.FindDeep(model, "dm_glow");
            var stateGlow = g ? PezShade.Emissive(g.gameObject) : new Renderer[0];
            bool cyan = loadGlow.Length > 0 && loadGlow[0].sharedMaterial != null && loadGlow[0].sharedMaterial.name.StartsWith("M_E_Cyan");
            core = cyan ? loadGlow : new Renderer[0];
            ore = cyan ? stateGlow : Concat(loadGlow, stateGlow);
            if (glows != null)
            {
                var own = new System.Collections.Generic.HashSet<Renderer>(Concat(core, ore));
                if (lamp) own.Add(lamp);
                glows = System.Array.FindAll(glows, r => !own.Contains(r));
            }
            all = System.Array.FindAll(model.GetComponentsInChildren<Renderer>(true), r => !(r is ParticleSystemRenderer));
            phase = seed * 0.37f;
            offset = Mathf.Repeat(seed * 0.29f, 1f);             // mines don't all work in step
            Pose(0f, 0f, 0f);
            if (kind == Kind.Belt) Belt(0f, true);
        }

        static Transform[] Numbered(Transform model, string prefix, int max)
        {
            var list = new System.Collections.Generic.List<Transform>();
            for (Transform c; list.Count < max && (c = PezMotion.FindDeep(model, prefix + list.Count)) != null; ) list.Add(c);
            return list.ToArray();
        }

        static Renderer[] Concat(Renderer[] a, Renderer[] b)
        {
            var r = new Renderer[a.Length + b.Length];
            a.CopyTo(r, 0); b.CopyTo(r, a.Length);
            return r;
        }

        public static DeepMineWorks For(GameObject model, int seed, ref Renderer[] glows) => new DeepMineWorks(model.transform, seed, ref glows);

        /// <summary>
        /// `sees`: the local view sees its live state (else it holds still, its lamp neutral). Effects go to Fx.Audience,
        /// which the caller sets.
        /// </summary>
        public void Tick(World world, Entity e, bool sees, float dt)
        {
            if (!sees) { Lamp(0.25f); Core(0.25f); Ore(0.8f); return; } // remembered under fog: no live state
            bool complete = e.IsComplete;
            var deposit = world.Map.DepositById(e.DepositId);
            int type = deposit != null ? deposit.Type : -1;
            if (type >= 0 && type != oreType) { Models.TintOre(model, type); oreType = type; }
            bool low = e.Team >= 0 && world.Teams[e.Team].LowPower;
            bool working = complete && e.Team >= 0 && e.Working;
            float frac = deposit == null ? 1f : deposit.Initial > 0f ? deposit.Amount / deposit.Initial : 0f;
            bool dry = deposit != null && deposit.Amount <= 0f;
            bool runningLow = deposit != null && !dry && deposit.Amount <= World.DeepMineWarnAmount;
            float now = Time.time, k = low ? 0.5f : 1f;

            // The machine: runs while working; stopped, it finishes the trip in hand and parks. A mine already at work when
            // first seen starts partway through a trip; anything still being built waits parked.
            if (!started && complete) { started = true; if (working) u = offset; if (dry) { dryT = 99f; beltFade = 0f; } }
            if (!complete) { u = 0f; running = false; }
            if (working) { running = true; empty = false; }
            // Stopped (dry): the trip in hand finishes, but empty: the load it was carrying is gone, so no ore arrives
            // after the sim says the deposit has none.
            else if (running && !empty) empty = true;
            if (kind == Kind.Belt) Belt(complete ? dt : 0f, working, k);
            else if (running && complete)
            {
                float rate = k / Trip;
                // On low power the winder labours: twice a trip, for a moment, the load slows to a crawl on its way up.
                if (low && u > Load && u < Up && Mathf.Repeat(now / 2.7f + phase, 1f) < 0.16f) rate *= 0.2f;
                float was = u;
                u += dt * rate;
                if (kind == Kind.Hoist && was < TipOut && u >= TipOut && !empty) Spill();
                if (kind == Kind.Jib && was < JRel && u >= JRel && !empty) { Spill(); if (carousel) { carouselFrom = carouselAngle; carouselT = 0f; } }
                if (u >= 1f) { u -= 1f; if (!working) { running = false; u = 0f; } }
            }
            if (kind == Kind.Hoist)
            {
                float s = SkipAt(u);
                Pose(s, TipAt(u), 0f);
                // The drum and the sheave turn with the rope.
                float ds = s - lastS; lastS = s;
                Rope(ds);
            }
            else if (kind == Kind.Jib)
            {
                JibAt(u, out float h, out float sw);
                Pose(h, 0f, sw);
                float ds = h - lastS; lastS = h;
                Rope(ds);
                // The carousel turns the next cask round after each rod, eased, in 0.8 s.
                if (carouselT >= 0f)
                {
                    carouselT += dt * k;
                    float c = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((carouselT - 0.2f) / 0.8f));
                    carouselAngle = carouselFrom + 90f * c;
                    if (carouselT >= 1f) { carouselT = -1f; carouselAngle = Mathf.Repeat(carouselFrom + 90f, 360f); }
                }
                if (carousel) carousel.localRotation = carouselRest * Quaternion.AngleAxis(carouselAngle, Vector3.up);
            }

            // The fan breathes while the mine works (half speed on low power) and coasts down when it stops; the drill string
            // turns slowly the same way.
            fanSpeed = Mathf.MoveTowards(fanSpeed, working ? 540f * k : 0f, dt * (working ? 360f : 180f));
            fanAngle = Mathf.Repeat(fanAngle + fanSpeed * dt, 360f);
            if (fan) fan.localRotation = fanRest * Quaternion.AngleAxis(fanAngle, Vector3.forward);
            drillSpeed = Mathf.MoveTowards(drillSpeed, working ? 50f * k : 0f, dt * (working ? 40f : 20f));
            drillAngle = Mathf.Repeat(drillAngle + drillSpeed * dt, 360f);
            if (drill) drill.localRotation = drillRest * Quaternion.AngleAxis(drillAngle, Vector3.up);

            Tumble(dt);
            // The bin: the heap bobs as each load lands; dry, it drains away.
            bob = Mathf.MoveTowards(bob, 0f, dt / 0.5f);
            drain = Mathf.MoveTowards(drain, complete && dry && !running ? 1f : 0f, dt / (dry ? 1.5f : 0.8f));
            if (heap && kind != Kind.Jib)
            {
                heap.localPosition = heapRest + Vector3.up * (0.02f * Mathf.Sin(Mathf.PI * bob));
                heap.localScale = Vector3.one * Mathf.Max(0.001f, 1f - drain);           // drains away into the bin
                if (heap.gameObject.activeSelf != drain < 0.99f) heap.gameObject.SetActive(drain < 0.99f);
            }

            // The stem: one piece per sixth of what's left.
            int want = frac <= 0f ? 0 : Mathf.Clamp(Mathf.CeilToInt(frac * Bricks - 0.001f), 1, Bricks);
            if (shown < 0) shown = want;                       // first sight: as it is, no pops
            if (popT < 0f && want != shown) { popT = 0f; adding = want > shown; }
            Stem(dt);

            // Run dry: a last puff of dust from the shaft as the machine stops, the lamp blinks three times and goes out.
            if (complete && dry && !wasDry && dryT < 0f) dryT = 0f;
            if (!dry) dryT = -1f;
            wasDry = dry;
            if (dryT >= 0f && dryT < 90f)
            {
                float was = dryT;
                dryT += dt;
                if (was < 0.6f && dryT >= 0.6f)
                    Fx.PourDust(model.transform.position + Vector3.up * 0.4f, WorldView.OreColors[Mathf.Clamp(oreType, 0, WorldView.OreColors.Length - 1)]);
            }

            // Dead: once it's dry and the lamp has gone out, the whole mine darkens over 2 s, so it reads as finished from
            // across the map. Ore again (only ever in the kitchen sink): it lights back up.
            dead = Mathf.MoveTowards(dead, complete && dry && (dryT < 0f || dryT >= 3f) ? 1f : 0f, dt / 2f);
            if (Mathf.Abs(dead - shownDead) > 0.01f) { shownDead = dead; PezShade.SetDim(all, Mathf.Lerp(1f, 0.6f, dead)); shownLamp = shownCore = shownOre = -1f; }

            // Glow is state.
            if (!complete) Lamp(0.05f);
            else if (dry) Lamp(dryT >= 0f && dryT < 3f && Mathf.Repeat(dryT, 1f) < 0.5f ? 1f : 0.03f);
            else if (runningLow) Lamp(Mathf.Lerp(0.15f, 0.9f * k, 0.5f + 0.5f * Mathf.Sin((now * 0.5f + phase) * Mathf.PI * 2f)));
            else Lamp(0.08f);
            Core(working ? k : 0.12f);
            Ore(complete && !dry ? k : 0.05f);
        }

        /// <summary>The skip's height above its rest over a hoist's trip.</summary>
        float SkipAt(float u)
        {
            if (u < Load) return 0f;
            if (u < Up) return rise * Mathf.SmoothStep(0f, 1f, (u - Load) / (Up - Load));
            if (u < TipBack) return rise;
            if (u < Down) return rise * (1f - Mathf.SmoothStep(0f, 1f, (u - TipBack) / (Down - TipBack)));
            return 0f;
        }

        /// <summary>The bucket's tip (0..1) over a hoist's trip.</summary>
        static float TipAt(float u)
        {
            if (u < Up || u >= TipBack) return 0f;
            if (u < TipOut) return Mathf.SmoothStep(0f, 1f, (u - Up) / (TipOut - Up));
            if (u < Pour) return 1f;
            return 1f - Mathf.SmoothStep(0f, 1f, (u - Pour) / (TipBack - Pour));
        }

        /// <summary>A jib's trip: the hook's height above its rest and the jib's swing (degrees).</summary>
        void JibAt(float u, out float h, out float sw)
        {
            static float S(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, (x - a) / (b - a));
            if (u < JUp0) { h = 0f; sw = 0f; }
            else if (u < JUp1) { h = rise * S(JUp0, JUp1, u); sw = 0f; }
            else if (u < JOut1) { h = rise; sw = swing * S(JUp1, JOut1, u); }
            else if (u < JLow1) { h = rise - lower * S(JOut1, JLow1, u); sw = swing; }
            else if (u < JRel) { h = rise - lower; sw = swing; }
            else if (u < JRaise1) { h = rise - lower * (1f - S(JRel, JRaise1, u)); sw = swing; }
            else if (u < JBack1) { h = rise; sw = swing * (1f - S(JRaise1, JBack1, u)); }
            else if (u < JHome1) { h = rise * (1f - S(JBack1, JHome1, u)); sw = 0f; }
            else { h = 0f; sw = 0f; }
        }

        void Rope(float ds)
        {
            if (sheave) { sheaveAngle = Mathf.Repeat(sheaveAngle + sheaveSign * ds / sheaveR * Mathf.Rad2Deg, 360f); sheave.localRotation = sheaveRest * Quaternion.AngleAxis(sheaveAngle, sheaveAxis); }
            if (drum) { drumAngle = Mathf.Repeat(drumAngle + drumSign * ds / drumR * Mathf.Rad2Deg, 360f); drum.localRotation = drumRest * Quaternion.AngleAxis(drumAngle, drumAxis); }
        }

        void Pose(float s, float tip, float sw)
        {
            if (skip) skip.localPosition = skipRest + Vector3.up * s;
            if (jib) jib.localRotation = jibRest * Quaternion.AngleAxis(sw, Vector3.up);
            if (bucket) bucket.localRotation = Quaternion.FromToRotation(Vector3.up, Vector3.up * Mathf.Cos(tip * Tip * Mathf.Deg2Rad) + tipDir * Mathf.Sin(tip * Tip * Mathf.Deg2Rad)) * bucketRest;
            if (load)
            {
                float k;
                if (kind == Kind.Jib)
                    // The rod rides out with the hook and goes into the cask; the next is picked up as the hook comes home.
                    k = u < JLow1 ? 1f : u < JRel ? 1f - (u - JLow1) / (JRel - JLow1) : u < JHome1 ? 0f : 1f;
                else
                    // The load rides up full and pours out as the bucket tips; it's back by the time the skip's down.
                    k = u < TipOut ? 1f : u < Pour + 0.02f ? 1f - (u - TipOut) / (Pour + 0.02f - TipOut) : u < Down ? 0f : 1f;
                if (empty || (kind == Kind.Jib && drain >= 1f)) k = 0f;   // stopped: nothing in hand; dry: no rod left to lift
                bool show = k > 0.02f;
                if (load.gameObject.activeSelf != show) load.gameObject.SetActive(show);
                if (show) load.localScale = kind == Kind.Jib ? new Vector3(1f, k, 1f) : Vector3.one * Mathf.Lerp(0.4f, 1f, k);
            }
            // The rope below the sheave reaches down to the skip's eye (its mesh is 1 long).
            if (rope && eye)
            {
                float len = (rope.position.y - eye.position.y) / Mathf.Max(1e-4f, rope.parent.lossyScale.y);
                rope.localScale = new Vector3(1f, Mathf.Max(0.001f, len), 1f);
            }
        }

        /// <summary>
        /// Copper's belt: the bricks ride from the tail to the head, one into the crusher a second (half that on low power),
        /// and the tail loads a new one only while the mine works, so a stopped belt runs its bricks off and then stops.
        /// </summary>
        void Belt(float dt, bool working, float k = 1f)
        {
            int n = carry.Length;
            if (n == 0) return;
            beltFade = Mathf.MoveTowards(beltFade, working ? 1f : 0f, dt / 0.5f);
            if (beltFade <= 0f) for (int i = 0; i < n; i++) carried[i] = false;
            bool any = false;
            for (int i = 0; i < n; i++) any |= carried[i];
            float target = working ? BeltBricksPerSecond * k : 0f;
            beltSpeed = Mathf.MoveTowards(beltSpeed, target, dt * 2f);
            float was = beltS;
            beltS += dt * beltSpeed / n;                        // in belt lengths: a brick every 1/n of the belt
            for (int i = 0; i < n; i++)
            {
                float t0 = Mathf.Repeat(was + (i + 0.5f) / n, 1f), t1 = Mathf.Repeat(beltS + (i + 0.5f) / n, 1f);
                if (t1 < t0)
                {
                    // Past the head: into the crusher. Then round to the tail, where it's loaded again if the mine still works.
                    if (carried[i] && working) Crush();
                    carried[i] = working;
                }
                var c = carry[i];
                // In at the tail out of the shaft, off at the head into the crusher: each grows and shrinks over the ends.
                float grow = Mathf.Clamp01(t1 / 0.08f) * Mathf.Clamp01((1f - t1) / 0.06f);
                bool show = carried[i] && grow > 0.02f;
                if (c.gameObject.activeSelf != show) c.gameObject.SetActive(show);
                if (show)
                {
                    c.localPosition = Vector3.Lerp(beltA.localPosition, beltB.localPosition, t1);
                    c.localScale = Vector3.one * Mathf.Lerp(0.3f, 1f, grow) * Mathf.Max(0.001f, beltFade);
                }
            }
            // The pulleys turn with the belt; the crusher turns while it runs.
            float travel = dt * beltSpeed / n * beltLen;
            pulleyAngle += travel / 0.05f * Mathf.Rad2Deg;
            for (int i = 0; i < pulleys.Length; i++) pulleys[i].localRotation = pulleyRest[i] * Quaternion.AngleAxis(pulleyAngle, Vector3.right);
            crusherAngle = Mathf.Repeat(crusherAngle + dt * 200f * beltSpeed, 360f);
            if (crusher) crusher.localRotation = crusherRest * Quaternion.AngleAxis(crusherAngle, Vector3.right);
        }

        /// <summary>A copper brick drops into the crusher: the heap below bobs, and a puff of mint dust.</summary>
        void Crush()
        {
            bob = 1f;
            if (crusher) Fx.PourDust(crusher.position, PezPalette.OreCopperPatina);
        }

        /// <summary>The load lands: in the bin (hoist) or the cask (jib). Each ore lands its own way.</summary>
        void Spill()
        {
            bob = 1f;
            if (!bin) return;
            Color32 c = WorldView.OreColors[Mathf.Clamp(oreType, 0, WorldView.OreColors.Length - 1)];
            if (kind == Kind.Jib) { Fx.Steam(bin.position + Vector3.up * 0.05f, 0.25f); return; }    // the cask's seal hisses shut
            if (oreType == 2) { Fx.ShardChime(bin.position, c); return; }                          // the shards ring as they land
            if (chute && tumble.Length > 0) { tumbleT = 0f; return; }   // iron: the bricks tumble down the chute first (Tumble)
            Fx.PourDust(bin.position, c);
        }

        /// <summary>Iron's dump: the bricks tumble out of the dump door one after another, down the chute and into the
        /// hopper, end over end; the heap bobs and a puff of cinnamon dust rises as the last lands.</summary>
        void Tumble(float dt)
        {
            if (tumbleT < 0f) return;
            tumbleT += dt;
            var from = chute.localPosition;
            var to = chute.parent.InverseTransformPoint(bin.position);
            bool any = false;
            for (int i = 0; i < tumble.Length; i++)
            {
                float f = (tumbleT - 0.09f * i) / 0.55f;
                bool show = f > 0f && f < 1f;
                any |= f < 1f;
                if (tumble[i].gameObject.activeSelf != show) tumble[i].gameObject.SetActive(show);
                if (!show) continue;
                // Down the chute, gathering speed, a little hop as each turns over.
                float k = f * f;
                tumble[i].localPosition = Vector3.Lerp(from, to, k) + Vector3.up * (0.05f * Mathf.Abs(Mathf.Sin(f * Mathf.PI * 2.5f)) + 0.02f * i);
                tumble[i].localRotation = Quaternion.AngleAxis(420f * f + 50f * i, new Vector3(0.3f, 0f, 1f).normalized);
            }
            if (!any)
            {
                tumbleT = -1f; bob = 1f;
                Fx.PourDust(bin.position, WorldView.OreColors[Mathf.Clamp(oreType, 0, WorldView.OreColors.Length - 1)]);
            }
        }

        /// <summary>
        /// The stem: `shown` pieces stacked from the top on the spring. A pop takes the top piece out (the head snaps open,
        /// the piece flips out over the lip, the stack rides up a slot, the head drops shut with a bounce); a refill (only ever
        /// in the kitchen sink) pushes one in from the top, the stack giving a slot. On an empty stem the head hangs open.
        /// </summary>
        void Stem(float dt)
        {
            int n = shown;
            float first = 0f;      // the slot the top piece sits in (0 = the top slot, + is down)
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
                if (!adding && t >= 0.1f) { n = shown - 1; first = 1f - k; }   // the top piece is the candy now; the rest ride up
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
            // The follower sits under the lowest piece (on an empty stem, up under the head); the spring reaches up to it.
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
            if (lamp) PezShade.Set(lamp, Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.25f, 1f, g)) * Mathf.Lerp(1f, 0.6f, dead), g);
        }

        /// <summary>Crystal's core: cyan only while the mine works.</summary>
        void Core(float g)
        {
            if (core.Length == 0 || Mathf.Abs(g - shownCore) < 0.005f) return;
            shownCore = g;
            PezShade.Set(core, Mathf.Lerp(0.35f, 1f, Mathf.InverseLerp(0.1f, 1f, g)) * Mathf.Lerp(1f, 0.6f, dead), g);
        }

        /// <summary>Uranium's rods and sight glasses: sour acid is always lit while there's any (half on low power).</summary>
        void Ore(float g)
        {
            if (ore.Length == 0 || Mathf.Abs(g - shownOre) < 0.005f) return;
            shownOre = g;
            PezShade.Set(ore, Mathf.Lerp(0.3f, 1f, Mathf.InverseLerp(0.05f, 1f, g)) * Mathf.Lerp(1f, 0.6f, dead), g);
        }
    }
}
