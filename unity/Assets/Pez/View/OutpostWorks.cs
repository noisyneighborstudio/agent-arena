using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The outpost, a forward ore depot (MOTION.md outpost), run from the sim: what the truck in its bay is doing, the ore
    /// it carries, and the enemies in the outpost's sight.
    ///  - The hopper's team trapdoors (flap_l, flap_r, hinged on the rim's sides) swing open (0.4 s, a little overshoot)
    ///    when a truck lines up in the bay, stay open while it backs in and tips, and slam shut 0.3 s after it pulls out.
    ///  - Every 20 ore that leaves the truck's bin (the sim tips 10 every 0.1 s) a lump of that ore comes up out of the
    ///    hopper onto the belt (feed_in) about 0.4 s later, as the truck's pour lands, and rides the belt up to its head
    ///    (feed_out) at 0.7 tiles/s; the head drum (drum) turns with the belt. At the top it's tossed over the stem's lip
    ///    into the stem (drop).
    ///  - The team flip-top head (head, hinged at its back edge) lifts 55 deg as the first lump nears the top, holds open
    ///    while the ore comes in, and drops shut with a bounce 0.5 s after the last.
    ///  - Every second lump in, a pressed brick of that ore lands on the stack behind the stem's window (silo_0..4, up to
    ///    five). Between deliveries the stack is fed away underground: every 3.5 s it slides down a slot and the bottom
    ///    brick leaves the window, down to one.
    ///  - The lookout (lookout, on its telescoping mast) sweeps the ground in front of the outpost (+-80 deg around the
    ///    bay side, 25 deg/s, a pause at each end): its sight is the outpost's job (vision 9). An enemy unit it can see within
    ///    that range draws it round (up to 120 deg/s) to follow the nearest.
    ///  - Deploying: while the outpost truck's bunker rises into the outpost (its stages below their rest), the mast is
    ///    down; once the top stage lands the mast telescopes up (0.6 s, a little overshoot).
    ///  - Under fog (a render whose team only remembers it) the belt is empty and nothing moves.
    /// The outpost makes its own power (+10), so low power doesn't slow it. A model without these nodes gets the parts it has.
    /// </summary>
    public class OutpostWorks
    {
        const float BeltSpeed = 0.7f, LumpSize = 0.09f, OrePerLump = 20f, PourDelay = 0.4f, DropTime = 0.35f;
        const float FlapOpen = 105f, HeadOpen = 55f, Pitch = 0.085f, DrainEvery = 3.5f, MastDrop = 0.24f;

        readonly Transform head, drum, lookout, mast, stage3, belt, silo;
        readonly Transform[] flaps, bricks;
        readonly Quaternion[] flapRest;
        readonly float[] flapSign;
        readonly Quaternion headRest, drumRest, lookRest;
        readonly Vector3 from, to, drop, mastRest;
        readonly Vector3[] slots;
        readonly int[] brickOre;
        readonly Material oreTemplate;
        readonly float phase, beltLen;

        struct Lump { public Transform T; public float U, Drop; }
        readonly List<Lump> riding = new List<Lump>();
        readonly Stack<Transform> spare = new Stack<Transform>();
        readonly Queue<(float at, int ore)> due = new Queue<(float, int)>();

        int dockTruck, lastCargo, ore = 2, stacked, pending;
        float flapK, flapClose = -1f, headK, headShut = -1f, headBounce = -1f, drumA, beltV, oreOwed;
        float drainT, landT = -1f, sweep, look, lookV, holdT, scanNext, mastK = 1f, mastT = -1f;
        int sweepDir = 1, target;
        bool shown = true, sawDown;

        OutpostWorks(Transform model, int seed)
        {
            head = PezMotion.FindDeep(model, "head");
            drum = PezMotion.FindDeep(model, "drum");
            lookout = PezMotion.FindDeep(model, "lookout");
            mast = PezMotion.FindDeep(model, "lookout_mast");
            stage3 = PezMotion.FindDeep(model, "stage_3");
            silo = PezMotion.FindDeep(model, "silo");
            if (head) headRest = head.localRotation;
            if (drum) drumRest = drum.localRotation;
            if (lookout) lookRest = lookout.localRotation;
            if (mast) mastRest = mast.localPosition;
            phase = seed * 0.37f;

            // The trapdoors: each opens the way that lifts its leaf (found from its mesh, whatever mirroring the import did).
            var fl = new List<Transform>();
            foreach (var n in new[] { "flap_l", "flap_r" }) { var f = PezMotion.FindDeep(model, n); if (f) fl.Add(f); }
            flaps = fl.ToArray();
            flapRest = new Quaternion[flaps.Length]; flapSign = new float[flaps.Length];
            for (int i = 0; i < flaps.Length; i++)
            {
                var f = flaps[i];
                flapRest[i] = f.localRotation;
                var r = f.GetComponentInChildren<Renderer>();
                var leaf = r ? f.InverseTransformPoint(r.bounds.center) : Vector3.left * 0.15f;
                var up = f.parent.TransformDirection(f.localRotation * Quaternion.Euler(0f, 0f, 90f) * leaf);
                flapSign[i] = up.y >= 0f ? 1f : -1f;
            }

            // The stack in the stem.
            var bl = new List<Transform>();
            for (Transform b; (b = PezMotion.FindDeep(model, "silo_" + bl.Count)) != null; ) bl.Add(b);
            bricks = bl.ToArray();
            slots = new Vector3[bricks.Length]; brickOre = new int[bricks.Length];
            for (int i = 0; i < bricks.Length; i++) { slots[i] = bricks[i].localPosition; brickOre[i] = 2; }
            if (bricks.Length > 0) oreTemplate = bricks[0].GetComponentInChildren<Renderer>()?.sharedMaterial;
            stacked = bricks.Length > 0 ? 1 + Mathf.Abs(seed) % 2 : 0;

            // The belt's ends and where the ore falls into the stem, in the model's space.
            var a = PezMotion.FindDeep(model, "feed_in");
            var b2 = PezMotion.FindDeep(model, "feed_out");
            var d = PezMotion.FindDeep(model, "drop");
            if (a && b2 && d && oreTemplate != null)
            {
                from = model.InverseTransformPoint(a.position) + Vector3.up * LumpSize * 0.5f;
                to = model.InverseTransformPoint(b2.position) + Vector3.up * LumpSize * 0.5f;
                drop = model.InverseTransformPoint(d.position);
                beltLen = Mathf.Max(0.1f, Vector3.Distance(from, to));
                belt = new GameObject("outpost_belt").transform;
                belt.SetParent(model, false);
            }
            Pose(0f);
        }

        public static OutpostWorks For(GameObject model, int seed) => new OutpostWorks(model.transform, seed);

        /// <summary>`sees`: the local view sees its live state (else nothing moves and the belt is empty).</summary>
        public void Tick(World w, Entity e, bool sees, float dt)
        {
            Deploy(dt);
            if (belt && sees != shown) { belt.gameObject.SetActive(sees); shown = sees; }
            if (!sees) return;
            float now = Time.time;

            // The bay: the truck delivering here, from the sim's dock steps.
            // Lining up, backing in or tipping (or tipping beside it when its lane is built over: WorldView.ReportDock).
            Entity truck = null;
            bool pulling = false;
            foreach (var o in w.Entities)
            {
                if (o.Dead || !o.IsHarvester || o.DockAt != e.Id || o.Order != Order.ReturnOre) continue;
                if (o.Dock == DockStep.PullOut) { pulling = true; continue; }
                bool inBay = o.Dock >= DockStep.Align && o.Dock <= DockStep.Unload;
                if (inBay || (!o.Moving && o.Cargo > 0 && Vec2.Dist(o.Pos, BayPoint(w, e, now)) <= 0.7f)) { truck = o; break; }
            }
            bool bay = truck != null, gone = truck == null && !pulling;
            if (truck != null)
            {
                if (truck.Id != dockTruck) { dockTruck = truck.Id; lastCargo = truck.Cargo; }
                if (truck.CargoType >= 0) ore = truck.CargoType;
                if (truck.Cargo < lastCargo)
                {
                    // Ore left the bin: a lump comes up out of the hopper as the pour lands in it.
                    oreOwed += lastCargo - truck.Cargo;
                    while (oreOwed >= OrePerLump * 0.5f) { oreOwed -= OrePerLump; due.Enqueue((now + PourDelay + Random.Range(0f, 0.1f), ore)); }
                }
                lastCargo = truck.Cargo;
            }
            else if (gone) { dockTruck = 0; oreOwed = 0f; }

            Flaps(bay, dt);
            Belt(now, dt);
            Head(dt);
            Stack(now, dt);
            Lookout(w, e, now, dt);
        }

        Vec2 bayPt;
        float bayAt = -10f;

        /// <summary>The sim's bay point (refreshed every 2 s: building over a lane moves it).</summary>
        Vec2 BayPoint(World w, Entity e, float now)
        {
            if (now - bayAt > 2f) { bayPt = w.Bay(e).bay; bayAt = now; }
            return bayPt;
        }

        // ---------------------------------------------------------------- the hopper's trapdoors

        void Flaps(bool bay, float dt)
        {
            if (flaps.Length == 0) return;
            if (bay) flapClose = 0.3f;
            else if (flapClose > 0f) flapClose -= dt;
            bool open = bay || flapClose > 0f;
            flapK = Mathf.MoveTowards(flapK, open ? 1f : 0f, dt / (open ? 0.4f : 0.3f));
            float a;
            if (open) { float x = flapK - 1f; a = 1f + 2.2f * x * x * x + 1.2f * x * x; }   // ease out with a small overshoot
            else a = flapK * flapK;                                                          // falls shut, faster at the end
            for (int i = 0; i < flaps.Length; i++)
                flaps[i].localRotation = flapRest[i] * Quaternion.Euler(0f, 0f, flapSign[i] * FlapOpen * a);
        }

        // ---------------------------------------------------------------- the belt and the ore on it

        void Belt(float now, float dt)
        {
            if (!belt) return;
            while (due.Count > 0 && due.Peek().at <= now)
            {
                var (_, kind) = due.Dequeue();
                var t = spare.Count > 0 ? spare.Pop() : NewLump();
                t.gameObject.SetActive(true);
                Models.TintOre(t.gameObject, kind);
                t.localScale = Vector3.zero;
                riding.Add(new Lump { T = t, U = 0f, Drop = -1f });
                pending++;
            }
            // The belt runs while ore rides it (up to speed in 0.3 s, down in 0.5 s).
            bool load = false;
            foreach (var l in riding) if (l.Drop < 0f) { load = true; break; }
            beltV = Mathf.MoveTowards(beltV, load ? BeltSpeed : 0f, dt * BeltSpeed / (load ? 0.3f : 0.5f));
            if (drum && beltV > 0f)
            {
                drumA = Mathf.Repeat(drumA + beltV / 0.05f * Mathf.Rad2Deg * dt, 360f);
                drum.localRotation = drumRest * Quaternion.Euler(drumA, 0f, 0f);
            }
            float du = beltV / beltLen * dt;
            for (int i = riding.Count - 1; i >= 0; i--)
            {
                var l = riding[i];
                if (l.Drop < 0f)
                {
                    l.U += du;
                    if (l.U >= 1f) { l.U = 1f; l.Drop = 0f; }
                    // Up out of the hopper's throat, then on up the belt.
                    float s = LumpSize * Mathf.SmoothStep(0f, 1f, l.U / 0.1f);
                    l.T.localPosition = Vector3.Lerp(from, to, l.U);
                    l.T.localScale = Vector3.one * s;
                }
                else
                {
                    // Tossed off the belt's head, over the lip and down into the stem.
                    l.Drop += dt / DropTime;
                    float k = Mathf.Clamp01(l.Drop);
                    var p = Vector3.Lerp(to, drop, k);
                    p.y += 0.16f * Mathf.Sin(Mathf.PI * Mathf.Min(1f, k * 1.2f));
                    l.T.localPosition = p;
                    l.T.localRotation = Quaternion.Euler(220f * k, 40f * k, 0f);
                    if (k >= 1f)
                    {
                        l.T.gameObject.SetActive(false);
                        spare.Push(l.T);
                        riding.RemoveAt(i);
                        pending--;
                        Landed();
                        continue;
                    }
                }
                riding[i] = l;
            }
        }

        Transform NewLump()
        {
            var t = Models.Part(belt, PrimitiveType.Cube, from, Vector3.zero, oreTemplate,
                new Vector3(Random.Range(-20f, 20f), Random.Range(0f, 90f), Random.Range(-20f, 20f)));
            t.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return t;
        }

        // ---------------------------------------------------------------- the flip-top head

        void Head(float dt)
        {
            if (!head) return;
            // Open while ore is coming over the top: from when a lump is 0.35 s from the belt's head.
            bool want = false;
            float near = 1f - 0.35f * BeltSpeed / beltLen;
            foreach (var l in riding) if (l.Drop >= 0f || l.U >= near) { want = true; break; }
            if (want) { headShut = 0.5f; headBounce = -1f; }
            else if (headShut > 0f) { headShut -= dt; if (headShut <= 0f && headK > 0.5f) headBounce = 0f; }
            float a;
            if (want || headShut > 0f)
            {
                headK = Mathf.MoveTowards(headK, 1f, dt / 0.2f);
                a = Mathf.SmoothStep(0f, 1f, headK);
            }
            else if (headBounce >= 0f)
            {
                // Falls shut and bounces twice on the hinge.
                headBounce += dt;
                float v = headBounce;
                a = Mathf.Abs(Mathf.Cos(v * 7f)) * Mathf.Exp(-v * 5f);
                headK = a;
                if (v > 1.1f) { headBounce = -1f; headK = 0f; a = 0f; }
            }
            else { headK = Mathf.MoveTowards(headK, 0f, dt / 0.2f); a = Mathf.SmoothStep(0f, 1f, headK); }
            head.localRotation = headRest * Quaternion.Euler(HeadOpen * a, 0f, 0f);
        }

        // ---------------------------------------------------------------- the stack behind the window

        int landedLumps;

        void Landed()
        {
            if (++landedLumps % 2 != 0) return;
            if (stacked >= bricks.Length) { drainT = 0f; return; }  // full: this one goes straight on down
            brickOre[stacked] = ore;
            Models.TintOre(bricks[stacked].gameObject, ore);
            stacked++;
            landT = 0f;
            drainT = 0f;
        }

        void Stack(float now, float dt)
        {
            if (bricks.Length == 0) return;
            bool busy = pending > 0 || due.Count > 0 || dockTruck != 0;
            float slide = 0f;
            if (busy) drainT = 0f;
            else if (stacked > 1)
            {
                drainT += dt;
                if (drainT >= DrainEvery)
                {
                    // Fed away underground: the stack slides down a slot and the bottom brick leaves the window.
                    float k = Mathf.Clamp01((drainT - DrainEvery) / 0.6f);
                    slide = Mathf.SmoothStep(0f, 1f, k);
                    if (k >= 1f)
                    {
                        for (int i = 0; i < stacked - 1; i++)
                            if (brickOre[i] != brickOre[i + 1]) { brickOre[i] = brickOre[i + 1]; Models.TintOre(bricks[i].gameObject, brickOre[i]); }
                        stacked--;
                        slide = 0f;
                        drainT = 0f;
                    }
                }
            }
            if (landT >= 0f) { landT += dt; if (landT > 0.25f) landT = -1f; }
            Pose(slide);
        }

        void Pose(float slide)
        {
            for (int i = 0; i < bricks.Length; i++)
            {
                bool show = i < stacked;
                if (bricks[i].gameObject.activeSelf != show) bricks[i].gameObject.SetActive(show);
                if (!show) continue;
                var p = slots[i] + Vector3.down * Pitch * slide;
                if (i == stacked - 1 && landT >= 0f) p += Vector3.up * 0.12f * (1f - Mathf.SmoothStep(0f, 1f, landT / 0.25f)); // drops onto the stack
                bricks[i].localPosition = p;
            }
        }

        // ---------------------------------------------------------------- the lookout

        void Lookout(World w, Entity e, float now, float dt)
        {
            if (!lookout) return;
            if (now >= scanNext)
            {
                // The nearest enemy unit the team can see within the outpost's sight.
                scanNext = now + 0.5f;
                target = 0;
                float best = e.Def.Sight;
                foreach (var o in w.Entities)
                {
                    if (o.Dead || o.IsStructure || o.Team < 0 || o.Team == e.Team || o.IsCarried) continue;
                    float dd = Vec2.Dist(o.Pos, e.Center);
                    if (dd > best || !w.IsVisibleTo(e.Team, o)) continue;
                    best = dd; target = o.Id;
                }
            }
            var tgt = target != 0 ? w.Get(target) : null;
            float want;
            if (tgt != null)
            {
                var p = lookout.parent;
                var l = p.InverseTransformPoint(new Vector3(tgt.Pos.X, lookout.position.y, tgt.Pos.Y)) - lookout.localPosition;
                var f = lookRest * Vector3.back;                      // the lens looks along the lookout's -z
                want = Mathf.DeltaAngle(0f, Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg - Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg);
                lookV = Mathf.Clamp(Mathf.DeltaAngle(look, want) * 5f, -120f, 120f);
                sweep = want;
            }
            else
            {
                // The sweep: across the bay side and back, pausing at each end.
                if (holdT > 0f) { holdT -= dt; lookV = Mathf.MoveTowards(lookV, 0f, 200f * dt); }
                else
                {
                    lookV = Mathf.MoveTowards(lookV, 25f * sweepDir, 60f * dt);
                    if (look * sweepDir >= 80f) { sweepDir = -sweepDir; holdT = 1f + Mathf.Repeat(phase, 0.6f); }
                }
            }
            look = Mathf.Clamp(look + lookV * dt, -180f, 180f);
            lookout.localRotation = Quaternion.Euler(0f, look, 0f) * lookRest;
        }

        // ---------------------------------------------------------------- deploying

        void Deploy(float dt)
        {
            if (!mast || !stage3) return;
            // The outpost truck's bunker rising into it: its top stage is still below its rest. Keep the mast down, then
            // telescope it up once the stage has landed.
            if (stage3.localPosition.y < -0.01f) { mastK = 0f; sawDown = true; mastT = -1f; }
            else if (sawDown && mastT < 0f) { mastT = 0f; sawDown = false; }
            if (mastT >= 0f)
            {
                mastT += dt;
                float k = Mathf.Clamp01((mastT - 0.15f) / 0.6f), x = k - 1f;
                mastK = 1f + 2.4f * x * x * x + 1.4f * x * x;
                if (k >= 1f) { mastK = 1f; mastT = -1f; }
            }
            mast.localPosition = mastRest + Vector3.down * MastDrop * (1f - mastK);
        }
    }
}
