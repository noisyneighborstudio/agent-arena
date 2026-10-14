using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The one owner of a ground vehicle's running gear and hull feel (MOTION.md: the candy chassis). Every ground vehicle
    /// gets one; it drives what the model has:
    ///  - The rig's Body (the visual child of the gameplay root, never the root) follows the ground under it: pitch and roll
    ///    from the plinth heights at its bow, stern and flanks (a factory's or refinery's driveway ramp), eased at 8/s. It
    ///    also carries the start squat (heavy tank), the artillery's deploy squat and the laden mining truck's squat.
    ///  - Hull feel: the sim's speed, smoothed, gives an acceleration; it pitches the hull nose-up pulling away and
    ///    nose-down braking (and about 0.3 s before the sim stops it), on an underdamped spring per weight class (light
    ///    bobs twice, heavy rocks once slowly, artillery settles), and each shot kicks it nose-up (Kick). On a candy-chassis
    ///    model that's the sprung `hull` node (the running gear stays on the ground), which also rolls into turns (up to 4
    ///    deg on wheels, 1.5 on tracks) and, on wheels, bounces on its springs (heave spring k 120, damping 10, driven by
    ///    ground noise with the speed). Older models pitch the whole Body as before.
    ///  - Running gear, from the motion actually drawn (so the roll-out and a resync's ease turn it too): each side's travel
    ///    is the forward speed plus or minus the yaw rate times its half gauge, so a pivot turn counter-runs the sides.
    ///    Treads (tread_l/_r) slide back along the belt's top run and wrap every TreadPitch; sprockets, idlers and wheels
    ///    turn by travel / radius. Every step is capped at 0.4 of the part's visual period per 1/30 s (and per frame), so
    ///    nothing strobes backwards on a 30 fps stream: the treads run at most 1.2 tiles/s, a little slower than a fast
    ///    vehicle's ground speed, always the right way. Steered wheels (steer_*) yaw into the turn (Ackermann from the yaw rate, 28 deg at most;
    ///    full lock in a pivot), easing at 220 deg/s.
    ///  - Dust: puffs off the tail on the move (more often the faster it goes), and a scrub of dust off the track ends or
    ///    tyres when it pivots on the spot. Exhaust: a few puffs out of the `exhaust` mouth as it pulls away, and an idle
    ///    wisp every 3-5 s. Effects go only to the cameras that can see it (WorldView.AudienceFor).
    ///  - Lid(open): the flip-top's crown (lid) opens 70 deg over 0.35 s for a vehicle's act, and shuts with a bounce.
    /// It owns the Body's rotation and position, the hull node, every tread, sprocket, idler, wheel and steer node, and
    /// the lid. PezMotion keeps the turret and barrel.
    /// </summary>
    public class VehicleWorks
    {
        /// <summary>The tread cleats' spacing (build_models.py CHASSIS_TREAD_PITCH).</summary>
        public const float TreadPitch = 0.1f;
        const float MaxSteer = 28f, SteerRate = 220f, LidOpen = 70f;

        /// <summary>A hull's spring per weight class: period, damping, nose-up and nose-down limits (deg), the kick a
        /// shot gives it (deg), and how far it rolls into a turn (deg, kit hulls only).</summary>
        struct HullClass { public float Period, Damping, Up, Down, Kick, Roll; }
        static readonly HullClass LightHull = new HullClass { Period = 0.25f, Damping = 0.35f, Up = 2f, Down = 3f, Kick = 1.5f, Roll = 1.5f };  // bobs twice
        static readonly HullClass HeavyHull = new HullClass { Period = 0.6f, Damping = 0.55f, Up = 1.5f, Down = 2.5f, Kick = 2.5f, Roll = 1f };   // one slow rock
        static readonly HullClass ArtyHull = new HullClass { Period = 0.5f, Damping = 0.7f, Up = 2f, Down = 3f, Kick = 3f, Roll = 1f };
        static readonly HullClass OtherHull = new HullClass { Period = 0.3f, Damping = 0.5f, Up = 2f, Down = 3f, Kick = 1.5f, Roll = 1.5f };

        static HullClass ClassOf(EntityDef d) => d.ModelKey switch
        {
            "light_tank" or "laser_tank" or "scout_buggy" => LightHull,
            "heavy_tank" or "mammoth_tank" => HeavyHull,
            "artillery" or "long_range_artillery" => ArtyHull,
            _ => OtherHull,
        };

        /// <summary>A part that turns with its side's travel: its rest, radius, side (-1 left, +1 right) and the angle per
        /// visual period (8 teeth: 45; three bolts: 120).</summary>
        class Turner { public Transform T; public Quaternion Rest; public float R, Side, Period, Angle; }
        class Tread { public Transform T; public Vector3 Rest; public float Side, Phase; }
        class Knuckle { public Transform T; public Quaternion Rest; }

        readonly Transform body, hull, exhaust, lid;
        readonly Quaternion hullRest, lidRest;
        readonly Vector3 hullPos;
        readonly Tread[] treads;
        readonly Turner[] turners;
        readonly Knuckle[] knuckles;
        readonly HullClass hc;
        readonly bool wheeled;
        readonly float length, width, gauge, wheelbase, seed;

        Vector3 lastPos; float lastYaw; bool primed;
        float simSpeed, drawnSpeed, yawRate;          // smoothed: the sim's speed (t/s), the drawn forward speed, yaw (deg/s)
        float pitch, pitchVel, roll, rollVel, heave, heaveVel, startSquat, travel;
        float slopePitch, slopeRoll, steer, lidK, lidGoal, lidBounce = -1f;
        float dustT, scrubT, wispAt, puffT, puffRest; int puffs;

        VehicleWorks(Rig rig, EntityDef def, int id)
        {
            body = rig.Body;
            var m = rig.Model != null ? rig.Model.transform : null;
            hc = ClassOf(def);
            seed = id * 0.618f;
            length = def.Radius * 2f; width = def.Radius * 1.5f;
            wispAt = Time.time + 2f + (id % 7) * 0.4f;
            if (m == null) { treads = new Tread[0]; turners = new Turner[0]; knuckles = new Knuckle[0]; return; }
            hull = PezMotion.FindDeep(m, "hull");
            if (hull) { hullRest = hull.localRotation; hullPos = hull.localPosition; }
            exhaust = PezMotion.FindDeep(m, "exhaust");
            lid = PezMotion.FindDeep(m, "lid");
            if (lid) lidRest = lid.localRotation;
            var tr = new System.Collections.Generic.List<Tread>();
            var tu = new System.Collections.Generic.List<Turner>();
            var kn = new System.Collections.Generic.List<Knuckle>();
            float zFront = float.MinValue, zBack = float.MaxValue;
            foreach (var sd in new[] { "l", "r" })
            {
                var t = PezMotion.FindDeep(m, "tread_" + sd);
                if (t) { tr.Add(new Tread { T = t, Rest = t.localPosition, Side = SideOf(m, t) }); gauge = Mathf.Max(gauge, Mathf.Abs(m.InverseTransformPoint(t.position).x)); }
                foreach (var (nm, period) in new[] { ("sprocket_", 45f), ("idler_", 120f) })
                    if (PezMotion.FindDeep(m, nm + sd) is Transform s) tu.Add(new Turner { T = s, Rest = s.localRotation, R = Radius(s), Side = SideOf(m, s), Period = period });
                for (int i = 0; i < 6; i++)
                {
                    var w = PezMotion.FindDeep(m, "wheel_" + sd + i);
                    if (!w) continue;
                    wheeled = true;
                    tu.Add(new Turner { T = w, Rest = w.localRotation, R = Radius(w), Side = SideOf(m, w), Period = 120f });
                    var p = m.InverseTransformPoint(w.position);
                    gauge = Mathf.Max(gauge, Mathf.Abs(p.x));
                    if (PezMotion.FindDeep(m, "steer_" + sd + i) is Transform k) { kn.Add(new Knuckle { T = k, Rest = k.localRotation }); zFront = Mathf.Max(zFront, p.z); }
                    else zBack = Mathf.Min(zBack, p.z);
                }
            }
            treads = tr.ToArray(); turners = tu.ToArray(); knuckles = kn.ToArray();
            wheelbase = zFront > float.MinValue && zBack < float.MaxValue ? zFront - zBack : length * 0.6f;
        }

        public static VehicleWorks For(Rig rig, EntityDef def, int id) => new VehicleWorks(rig, def, id);

        /// <summary>Which side a part is on in the model's own frame (Unity's: -x is the vehicle's left).</summary>
        static float SideOf(Transform model, Transform t) => model.InverseTransformPoint(t.position).x < 0f ? -1f : 1f;

        /// <summary>A turning part's radius: half its meshes' height (their bounds in its own space).</summary>
        static float Radius(Transform t)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var mf in t.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var p = t.InverseTransformPoint(mf.transform.TransformPoint(b.center + Vector3.Scale(b.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1))));
                    lo = Mathf.Min(lo, p.y); hi = Mathf.Max(hi, p.y);
                }
            }
            return hi > lo ? Mathf.Max(0.03f, (hi - lo) * 0.5f) : 0.1f;
        }

        /// <summary>A shot: the hull kicks back (nose up), an impulse on the spring, so it rides on any settling dip.</summary>
        public void Kick() => pitchVel -= hc.Kick * 6.2831853f / hc.Period;

        /// <summary>A jolt on the hull's pitch spring (deg/s; positive is nose down): the mining truck's bed slamming down.</summary>
        public void Bump(float degPerSecond) => pitchVel += degPerSecond;

        /// <summary>Open (true) or shut the flip-top's crown, for a vehicle's act (an APC's passengers, a deploy).</summary>
        public void Lid(bool open) => lidGoal = open ? 1f : 0f;

        public void Tick(WorldView view, WorldView.EV v, float dt)
        {
            if (dt <= 0f) return;
            var e = v.E;
            var root = v.Rig.Root;
            Feel(view, v, Mathf.Min(dt, 0.03f)); // the springs are explicit: they diverge at ~0.2 s frames, so keep the step small
            Slope(v, dt);
            // The motion actually drawn this frame, in the root's frame.
            var p = root.position;
            float yaw = root.eulerAngles.y;
            if (!primed) { lastPos = p; lastYaw = yaw; primed = true; }
            var d = p - lastPos; d.y = 0f;
            float fwd = Vector3.Dot(d, root.forward), dyaw = Mathf.DeltaAngle(lastYaw, yaw);
            lastPos = p; lastYaw = yaw;
            if (d.sqrMagnitude > 1f || Mathf.Abs(dyaw) > 90f) { fwd = 0f; dyaw = 0f; } // a teleport (unloaded, deployed), not driving
            drawnSpeed = Mathf.Lerp(drawnSpeed, fwd / dt, 1f - Mathf.Exp(-dt * 10f));
            yawRate = Mathf.Lerp(yawRate, dyaw / dt, 1f - Mathf.Exp(-dt * 10f));
            Gear(fwd, dyaw * Mathf.Deg2Rad, dt);
            Steering(dt);
            LidTick(dt);
            Fx.Audience = view.AudienceFor(e);
            Dust(v, dt);
            Exhaust(e, dt);
            Fx.Audience = null;
        }

        /// <summary>Pitch on the sim's acceleration and braking (a spring per weight class), roll into turns, bounce on wheels.</summary>
        void Feel(WorldView view, WorldView.EV v, float dt)
        {
            var e = v.E;
            float speed = Vec2.Dist(e.PrevPos, e.Pos) / World.Dt;
            float smoothed = Mathf.Lerp(simSpeed, speed, 1f - Mathf.Exp(-dt * 6f));
            float accel = (smoothed - simSpeed) / dt;
            simSpeed = smoothed;
            float target = Mathf.Clamp(-accel * 1.5f, -hc.Up, hc.Down); // nose-up is negative pitch
            if (e.Moving && view.AboutToStop(e)) target = hc.Down;
            float w = 6.2831853f / hc.Period;
            pitchVel += (w * w * (target - pitch) - 2f * hc.Damping * w * pitchVel) * dt;
            pitch = Mathf.Clamp(pitch + pitchVel * dt, -hc.Up * 1.6f, hc.Down * 1.6f);
            if (!(Mathf.Abs(pitch) < 30f)) pitch = pitchVel = 0f; // NaN / runaway: start over
            if (e.Def.ModelKey == "heavy_tank") startSquat = Mathf.MoveTowards(startSquat, accel > 0.4f ? 1f : 0f, dt / 0.25f);
            if (accel > 1.2f && puffs == 0 && puffRest <= 0f) puffs = 3; // pulling away: the stack coughs
            if (hull == null) return;
            // Kit hulls: roll out of the turn (centrifugal), bounded per class (4 deg on wheels), on the same spring.
            float rollMax = wheeled ? 4f : hc.Roll;
            float rollTarget = Mathf.Clamp(drawnSpeed * yawRate * Mathf.Deg2Rad * (wheeled ? 0.4f : 0.15f), -rollMax, rollMax); // deg per t/s^2 of turn
            rollVel += (w * w * (rollTarget - roll) - 2f * hc.Damping * w * rollVel) * dt;
            roll = Mathf.Clamp(roll + rollVel * dt, -rollMax, rollMax);
            if (!(Mathf.Abs(roll) < 30f)) roll = rollVel = 0f;
            if (wheeled)
            {
                // Springs: the body heaves on its suspension (k 120, damping 10), driven by ground noise as it drives.
                travel += Mathf.Abs(drawnSpeed) * dt;
                float rough = Mathf.Clamp01(Mathf.Abs(drawnSpeed) / 2f);
                float goal = (Mathf.PerlinNoise(travel * 2.6f, seed) - 0.5f) * 0.05f * rough;
                heaveVel += (120f * (goal - heave) - 10f * heaveVel) * dt;
                heave = Mathf.Clamp(heave + heaveVel * dt, -0.025f, 0.025f);
                if (float.IsNaN(heave)) heave = heaveVel = 0f;
            }
            hull.localRotation = hullRest * Quaternion.Euler(pitch, 0f, roll);
            hull.localPosition = hullPos + Vector3.up * heave;
        }

        /// <summary>The Body follows the plinth under it (ramps), and carries the squats.</summary>
        void Slope(WorldView.EV v, float dt)
        {
            var root = v.Rig.Root;
            var at = root.position;
            var f = root.forward * length * 0.5f;
            var r = root.right * width * 0.5f;
            float hb = Plinths.HeightAt(at - f), hf = Plinths.HeightAt(at + f), hl = Plinths.HeightAt(at - r), hr = Plinths.HeightAt(at + r);
            float tp = Mathf.Atan2(hb - hf, length) * Mathf.Rad2Deg, tr = Mathf.Atan2(hr - hl, width) * Mathf.Rad2Deg;
            float k = 1f - Mathf.Exp(-dt * 8f);
            slopePitch = Mathf.Lerp(slopePitch, tp, k);
            slopeRoll = Mathf.Lerp(slopeRoll, tr, k);
            var q = Quaternion.Euler(slopePitch, 0f, slopeRoll);
            body.localRotation = hull != null ? q : q * Quaternion.Euler(pitch, 0f, 0f);
            var bp = body.localPosition;
            bp.y = -0.015f * startSquat - 0.04f * v.Deploy - 0.03f * v.Squat;
            body.localPosition = bp;
        }

        /// <summary>Treads, sprockets, idlers and wheels: each side's travel, capped per frame below half its visual period.</summary>
        void Gear(float fwd, float dyawRad, float dt)
        {
            // At most 0.4 of a period per 1/30 s (and per frame): a 30 fps stream never sees one step backwards.
            float cap = Mathf.Min(0.4f, 12f * dt);
            foreach (var t in treads)
            {
                float s = Mathf.Clamp(fwd - t.Side * dyawRad * gauge, -cap * TreadPitch, cap * TreadPitch);
                t.Phase = Mathf.Repeat(t.Phase + s, TreadPitch);
                t.T.localPosition = t.Rest + Vector3.back * t.Phase;
            }
            foreach (var w in turners)
            {
                float s = fwd - w.Side * dyawRad * gauge;
                float step = Mathf.Clamp(s / w.R * Mathf.Rad2Deg, -cap * w.Period, cap * w.Period);
                w.Angle = Mathf.Repeat(w.Angle + step, 360f);
                w.T.localRotation = w.Rest * Quaternion.Euler(w.Angle, 0f, 0f);
            }
        }

        /// <summary>Front wheels yaw into the turn: Ackermann from the yaw rate at speed, full lock pivoting on the spot.</summary>
        void Steering(float dt)
        {
            if (knuckles.Length == 0) return;
            float goal;
            if (Mathf.Abs(drawnSpeed) < 0.3f) goal = Mathf.Abs(yawRate) > 20f ? MaxSteer * Mathf.Sign(yawRate) : steer * 0.98f; // pivoting: full lock; parked: holds
            else
            {
                float vEff = drawnSpeed >= 0f ? Mathf.Max(drawnSpeed, 0.3f) : Mathf.Min(drawnSpeed, -0.3f);
                goal = Mathf.Atan(wheelbase * yawRate * Mathf.Deg2Rad / vEff) * Mathf.Rad2Deg;
            }
            steer = Mathf.MoveTowards(steer, Mathf.Clamp(goal, -MaxSteer, MaxSteer), SteerRate * dt);
            foreach (var k in knuckles) k.T.localRotation = k.Rest * Quaternion.Euler(0f, steer, 0f);
        }

        void LidTick(float dt)
        {
            if (!lid) return;
            float was = lidK;
            lidK = Mathf.MoveTowards(lidK, lidGoal, dt / 0.35f);
            if (was > 0.02f && lidK <= 0f) lidBounce = 0f;
            float a = LidOpen * Mathf.SmoothStep(0f, 1f, lidK);
            if (lidBounce >= 0f)
            {
                lidBounce += dt;
                a += 6f * Mathf.Abs(Mathf.Sin(lidBounce * 16f)) * Mathf.Exp(-lidBounce * 7f);
                if (lidBounce > 0.8f) lidBounce = -1f;
            }
            lid.localRotation = lidRest * Quaternion.Euler(-a, 0f, 0f);
        }

        /// <summary>Dust off the tail on the move (as before), and a scrub of dust off the corners while it pivots on the spot.</summary>
        void Dust(WorldView.EV v, float dt)
        {
            var e = v.E;
            var root = v.Rig.Root;
            float r = e.Def.Radius;
            if (simSpeed >= 0.35f && (dustT -= dt) <= 0f)
            {
                dustT = Mathf.Lerp(0.3f, 0.1f, Mathf.Clamp01(simSpeed / 3f));
                float side = (Time.frameCount & 1) == 0 ? 1f : -1f;
                var at = root.position - root.forward * r * 0.8f + root.right * r * 0.5f * side;
                at.y = 0f;
                Fx.TrackDust(at, -root.forward, Mathf.Clamp(r * 2f, 0.7f, 2f));
            }
            else if (simSpeed < 0.35f) dustT = 0f;
            // Pivoting: the tracks (or tyres) scrub the ground at the bow and stern, thrown out sideways.
            if (Mathf.Abs(yawRate) > 60f && Mathf.Abs(drawnSpeed) < 0.5f && (scrubT -= dt) <= 0f)
            {
                scrubT = 0.14f;
                float end = (Time.frameCount & 1) == 0 ? 1f : -1f, turn = Mathf.Sign(yawRate);
                var at = root.position + root.forward * r * 0.75f * end + root.right * r * 0.6f * (Random.value < 0.5f ? 1f : -1f);
                at.y = 0f;
                Fx.TrackDust(at, root.right * turn * end, Mathf.Clamp(r * 1.6f, 0.6f, 1.6f));
            }
        }

        /// <summary>The stack coughs as it pulls away (three puffs, 0.12 s apart) and breathes a wisp now and then idling.</summary>
        void Exhaust(Entity e, float dt)
        {
            puffRest -= dt;
            if (!exhaust) { puffs = 0; return; }
            if (puffs > 0 && (puffT -= dt) <= 0f) { Fx.Exhaust(exhaust.position, 1f); puffT = 0.12f; if (--puffs == 0) puffRest = 1.5f; }
            if (Time.time >= wispAt)
            {
                if (simSpeed < 0.35f) Fx.Exhaust(exhaust.position, 0f);
                wispAt = Time.time + Random.Range(3f, 5f);
            }
        }
    }
}
