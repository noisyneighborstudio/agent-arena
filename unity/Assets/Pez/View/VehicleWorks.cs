using System.Collections;
using System.Collections.Generic;
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
    ///  - Vehicle acts that own their own nodes run from here: SurveyorAct (the surveyor's survey) and DrillRigAct (the
    ///    drill rig's lamp on the road, and its deploy).
    ///  - The minelayer's dispenser (ml_candy_*, ml_drop_candy and the lid): as it closes on its next spot the head flips
    ///    back; when the sim lays the mine (Laid) the candy stack steps down one, the bottom candy drops through the floor
    ///    with a puff under the hull, and the head shuts. The stack shows how many mines the team's steel still buys (6 at
    ///    most). All of it, the puff too, is its own team's to see: anyone else sees the head shut and a full, still stack.
    ///  - The work trucks (build_models.py work_cab: the repair truck) - Work(): the amber beacon on the cab's cap
    ///    (wt_beacon) is dark until the truck works, then flashes at 1.5 Hz and throws a small amber pool on the ground
    ///    (GlowCaster): the repair truck while its welds land (a "repair" event in the last 0.5 s: Welded). While the sim has
    ///    it parked at its patient in range (Order.Repair, stopped, within RepairRange) the boom (rt_boom) lifts 28 deg over
    ///    0.6 s and the jib (rt_jib) swings the torch (barrel) to point at the weld, 35 to 115 deg below the boom, at
    ///    140 deg/s; PezMotion turns the crane (turret) to the sim's TurretFacing. While welds land the boom sways +-2 deg on
    ///    a 1.4 s sine and the torch tip glows, flaring with each weld. Done, the jib swings back to hang the torch and the
    ///    boom settles onto its rest.
    ///  - HandOff: the one deploy hand-off. When the sim turns a vehicle into a structure, WorldView.Retire runs the
    ///    vehicle's own act here (the drill rig drilling itself down under its deep mine, DrillRigAct.Deploy; the
    ///    construction truck standing the command center's crane mast up on its footing, DeployInto), or the generic sink.
    /// It owns the Body's rotation and position, the hull node, every tread, sprocket, idler, wheel and steer node, the
    /// lid, the minelayer's candies, the repair truck's wt_beacon, rt_boom and rt_jib, and a deploying truck's carried
    /// part. PezMotion keeps the turret and barrel.
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
        // The construction truck: long, laden and slow, it rocks once, shallowly (its 1.5-long hull's ends travel the most;
        // at these limits its track guards stay clear of the sprockets and idlers).
        static readonly HullClass CarrierHull = new HullClass { Period = 0.7f, Damping = 0.6f, Up = 0.8f, Down = 1f, Kick = 0f, Roll = 0.75f };
        static readonly HullClass OtherHull = new HullClass { Period = 0.3f, Damping = 0.5f, Up = 2f, Down = 3f, Kick = 1.5f, Roll = 1.5f };

        static HullClass ClassOf(EntityDef d) => d.ModelKey switch
        {
            "light_tank" or "laser_tank" or "scout_buggy" => LightHull,
            "heavy_tank" or "mammoth_tank" or "drill_rig" => HeavyHull,
            "construction_truck" => CarrierHull,
            "artillery" or "long_range_artillery" => ArtyHull,
            _ => OtherHull,
        };

        /// <summary>A part that turns with its side's travel: its rest, radius, side (-1 left, +1 right) and the angle per
        /// visual period (8 teeth: 45; three bolts: 120).</summary>
        class Turner { public Transform T; public Quaternion Rest; public float R, Side, Period, Angle; }
        class Tread { public Transform T; public Vector3 Rest; public float Side, Phase; }
        class Knuckle { public Transform T; public Quaternion Rest; }

        readonly Transform body, hull, exhaust, lid, carried, carriedFoot;
        readonly Carry carry;
        readonly Quaternion hullRest, lidRest;
        readonly Vector3 hullPos;
        readonly Tread[] treads;
        readonly Turner[] turners;
        readonly Knuckle[] knuckles;
        readonly SurveyorAct survey;   // the surveyor's dish, thumper and lamps (its own sv_* nodes)
        readonly DrillRigAct drill;    // the drill rig's lamp on the road (its deploy runs from HandOff)
        readonly HullClass hc;
        readonly bool wheeled;
        readonly float length, width, gauge, wheelbase, seed;

        // The minelayer's dispenser (its act: see the summary).
        const float LaySlide = 0.25f;
        class Part { public Transform T; public Vector3 Rest; public Renderer[] R; public bool Shown = true; }
        readonly Part[] candies;
        readonly Part dropCandy;
        readonly bool secret;           // its act is its own team's business (the minelayer)
        float lidAngle, layT = 99f, candyPitch;
        int candiesShown = 6;
        bool owner = true;              // the camera being drawn is its team's (or a spectator's): it sees the private act
        static readonly List<int> ownerAudience = new List<int>();

        // The work trucks (see Work).
        readonly Renderer[] beacon, torch;
        readonly GlowCaster beaconLight;
        readonly Transform boom, jib;
        readonly Quaternion boomRest, jibRest;
        const float JibRest = 85f, BoomLift = 28f;
        float beaconK, beaconShown = -1f, torchShown = -1f, weldAt = -99f, weldFlare, boomK, jibAngle = JibRest;
        Vector3 weldPoint; bool hasWeld;

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
            if (m == null) { treads = new Tread[0]; turners = new Turner[0]; knuckles = new Knuckle[0]; candies = new Part[0]; return; }
            survey = SurveyorAct.For(m);
            drill = DrillRigAct.For(m);
            candies = Parts(m, "ml_candy_");
            if (PezMotion.FindDeep(m, "ml_drop_candy") is Transform dc) dropCandy = new Part { T = dc, Rest = dc.localPosition, R = dc.GetComponentsInChildren<Renderer>(true) };
            if (candies.Length > 1) candyPitch = candies[1].Rest.y - candies[0].Rest.y;
            secret = candies.Length > 0;
            if (PezMotion.FindDeep(m, "wt_beacon") is Transform bc)
            {
                beacon = PezShade.Emissive(bc.gameObject);
                beaconLight = GlowCaster.For(rig.Model, m.InverseTransformPoint(bc.position) + Vector3.up * 0.03f, PezPalette.EmissiveAmberIndustryDocking,
                                             light: 0.7f, range: 1.4f, pool: 0.5f);
            }
            boom = PezMotion.FindDeep(m, "rt_boom");
            jib = PezMotion.FindDeep(m, "rt_jib");
            if (boom) boomRest = boom.localRotation;
            if (jib) jibRest = jib.localRotation;
            var tb = PezMotion.FindDeep(m, "barrel");
            torch = jib && tb ? PezShade.Emissive(tb.gameObject) : null;
            hull = PezMotion.FindDeep(m, "hull");
            if (hull) { hullRest = hull.localRotation; hullPos = hull.localPosition; }
            exhaust = PezMotion.FindDeep(m, "exhaust");
            lid = PezMotion.FindDeep(m, "lid");
            if (lid) lidRest = lid.localRotation;
            if (CarryOf(def.ModelKey) is Carry c && PezMotion.FindDeep(m, c.Node) is Transform cn && PezMotion.FindDeep(cn, c.Foot) is Transform cf)
            {
                carry = c; carried = cn; carriedFoot = cf;
            }
            var tr = new List<Tread>();
            var tu = new List<Turner>();
            var kn = new List<Knuckle>();
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

        /// <summary>Numbered parts (prefix0, prefix1, ...) with their rests and renderers.</summary>
        static Part[] Parts(Transform m, string prefix)
        {
            var l = new List<Part>();
            for (Transform t; (t = PezMotion.FindDeep(m, prefix + l.Count)); )
                l.Add(new Part { T = t, Rest = t.localPosition, R = t.GetComponentsInChildren<Renderer>(true) });
            return l.ToArray();
        }

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

        /// <summary>A repair truck's weld landed at `at` (the sim's "repair" event, every 0.4 s while it repairs).</summary>
        public void Welded(Vector3 at) { weldAt = Time.time; weldFlare = 1f; weldPoint = at; hasWeld = true; }

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
            owner = view.PovTeam < 0 || view.PovTeam == e.Team;
            Dispenser(view, e, dt);
            LidTick(dt);
            ShowPrivate();
            Fx.Audience = view.AudienceFor(e);
            Dust(v, dt);
            Exhaust(e, dt);
            if (beacon != null || boom) Work(view, v, dt);
            survey?.Tick(view, v, this, dt);
            drill?.Tick(view, v, dt);
            Fx.Audience = null;
        }

        /// <summary>The work trucks' acts, from the sim: the beacon, and the repair truck's crane.</summary>
        void Work(WorldView view, WorldView.EV v, float dt)
        {
            var e = v.E;
            bool working = false;
            if (boom && jib)
            {
                // Repair: deployed while the sim has it stopped at its patient, in range; welding while its welds land.
                var t = e.Order == Order.Repair ? view.World.Get(e.TargetId) : null;
                bool atIt = t != null && !t.Dead && !e.Moving && t.DistFrom(e.Pos) <= e.Def.RepairRange + 0.05f;
                bool welding = atIt && Time.time - weldAt < 0.5f;
                working = welding;
                boomK = Mathf.MoveTowards(boomK, atIt ? 1f : 0f, dt / 0.6f);
                float sway = welding ? Mathf.Sin(Time.time * 6.2831853f / 1.4f) * 2f : 0f;
                boom.localRotation = boomRest * Quaternion.Euler(-BoomLift * Mathf.SmoothStep(0f, 1f, boomK) + sway * boomK, 0f, 0f);
                float goal = JibRest;
                if (atIt)
                {
                    // The torch points at the weld (its last landing, or the patient itself before the first).
                    var aim = hasWeld && welding ? weldPoint : WorldView.W(t.Center, t.IsAir && view.Views.TryGetValue(t.Id, out var tv) ? tv.Rig.Root.position.y : 0.4f);
                    var local = boom.InverseTransformPoint(aim) - jib.localPosition;
                    goal = Mathf.Clamp(Mathf.Atan2(-local.y, local.z) * Mathf.Rad2Deg, 35f, 115f);
                }
                jibAngle = Mathf.MoveTowards(jibAngle, goal, 140f * dt);
                jib.localRotation = jibRest * Quaternion.Euler(jibAngle - JibRest, 0f, 0f);
                if (!atIt) hasWeld = false;
                weldFlare = Mathf.MoveTowards(weldFlare, 0f, dt / 0.35f);
                float tg = welding ? 0.55f + 1.1f * weldFlare : 0f;
                if (torch != null && Mathf.Abs(tg - torchShown) > 0.01f) { PezShade.Set(torch, 1f, tg); torchShown = tg; }
            }
            if (beacon == null) return;
            // The beacon: a rotating lamp's flash (sharp, 1.5 Hz) while it works, fading in and out over 0.3 s.
            beaconK = Mathf.MoveTowards(beaconK, working ? 1f : 0f, dt / 0.3f);
            float flash = Mathf.Pow(Mathf.Max(0f, Mathf.Sin((Time.time + seed) * 6.2831853f * 1.5f)), 4f);
            float g = beaconK * (0.25f + 1.35f * flash);
            if (Mathf.Abs(g - beaconShown) > 0.01f) { PezShade.Set(beacon, 1f, g); beaconShown = g; }
            beaconLight?.Set(g, Fx.Audience);
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
            lidAngle = a;
            if (!secret) lid.localRotation = lidRest * Quaternion.Euler(-a, 0f, 0f);
        }

        // ------------------------------------------------------------------ the minelayer's dispenser

        /// <summary>The sim just laid a mine under it: the stack steps down and the bottom candy drops through the floor.</summary>
        public void Laid(WorldView view, Entity e)
        {
            if (candies.Length == 0) return;
            layT = 0f;
            var root = body.parent;
            if (root == null) return;
            Fx.Audience = OwnerAudience(view, e.Team);
            var g = root.position; g.y = 0f;
            Fx.MineDrop(g, e.Def.Radius);
            Fx.Audience = null;
        }

        /// <summary>The cameras that may see a vehicle's private act: the local view if it's that team's or a spectator's,
        /// and that team's own stream.</summary>
        static List<int> OwnerAudience(WorldView view, int team)
        {
            ownerAudience.Clear();
            if (view.PovTeam < 0 || view.PovTeam == team) ownerAudience.Add(TerrainView.MainFogLayer);
            if (team >= 0 && team < 8) ownerAudience.Add(TerrainView.TeamFogLayerBase + team);
            return ownerAudience;
        }

        void Dispenser(WorldView view, Entity e, float dt)
        {
            if (candies.Length == 0) return;
            layT += dt;
            // The head flips back as it closes on its next spot (lead with what's coming), and shuts once the candy is down.
            bool near = e.Order == Order.LayMines && e.MineQueue.Count > 0 && Vec2.Dist(e.Pos, e.MineQueue[0]) < 0.8f;
            Lid(near || layT < 0.45f);
            int steel = e.Team >= 0 && e.Team < view.World.Teams.Count ? view.World.Teams[e.Team].Amount("steel") : 0;
            candiesShown = Mathf.Clamp(steel / EntityDef.MineCost, 0, candies.Length);
        }

        /// <summary>
        /// Show the private parts of the act as the camera being drawn may see them (WorldView.SetPov calls this before
        /// each player's stream render and again after): its own team sees the dispenser at work; anyone else sees the head
        /// shut and a full, still stack.
        /// </summary>
        public void Show(bool sees) { owner = sees; ShowPrivate(); }

        void ShowPrivate()
        {
            if (candies.Length == 0) return;
            if (lid) lid.localRotation = lidRest * Quaternion.Euler(owner ? -lidAngle : 0f, 0f, 0f);
            float p = owner ? Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(layT / LaySlide)) : 1f;
            for (int i = 0; i < candies.Length; i++)
            {
                SetShown(candies[i], !owner || i < candiesShown);
                candies[i].T.localPosition = candies[i].Rest + Vector3.up * (candyPitch * (1f - p));
            }
            if (dropCandy != null)
            {
                bool falling = owner && layT < LaySlide + 0.05f;
                SetShown(dropCandy, falling);
                dropCandy.T.localPosition = dropCandy.Rest + Vector3.down * (0.08f * p);
            }
        }

        static void SetShown(Part s, bool on)
        {
            if (s.Shown == on) return;
            s.Shown = on;
            foreach (var r in s.R) if (r) r.enabled = on;
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

        // ---------------------------------------------------------------- deploying into a structure: the one hand-off

        /// <summary>
        /// The deploy hand-off. The sim turns a deploying vehicle into its structure in one tick, whole and working; WorldView
        /// .Retire takes the vehicle's model off its rig and hands it here with `into`, the structure just spawned. Every act
        /// starts the same way: the structure is set back to build progress 0, the model's PezMotion stops, and the act's
        /// effects go to the structure's audience, copied once now (AudienceFor reuses its list). Every act ends the same way
        /// too: the vehicle stands its part up off its stern, sinks under the ground while the structure rises over the spot
        /// through its build stages (PezEmerge build progress), and is destroyed out of sight, so nothing pops. In between,
        /// each machine does its own work:
        ///  - the drill rig (dr_* nodes) drills on the spot: the mast up, the auger out and biting, chips of the deposit's ore,
        ///    and the deposit's own deep mine rising as it drills itself down (DrillRigAct.Deploy);
        ///  - a truck that carries part of its structure (Carry: the construction truck's crane mast) lines up so the part's
        ///    foot is over that part's place in the structure, stands it up there and leaves it planted while the structure
        ///    rises round it (DeployInto).
        /// Returns null for a vehicle with no act of its own: WorldView plays the generic sink (PezEmerge.PlayDeployInto).
        /// </summary>
        public IEnumerator HandOff(WorldView view, Transform model, WorldView.EV into)
        {
            if (drill == null && carried == null) return null;
            var audience = new List<int>(view.AudienceFor(into.E));
            into.Rig.Emerge.SetBuildProgress(0f);
            if (model.GetComponent<PezMotion>() is PezMotion motion) motion.enabled = false;
            return drill != null ? DrillRigAct.Deploy(view, model, into, audience) : DeployInto(model, into.Rig.Model.transform, into.Rig.Emerge, audience);
        }

        /// <summary>
        /// What a deploying truck carries onto its site. `Node`, hinged on the tail and lying forward along the deck, is a
        /// part of the structure built the same way (build_models.py builds both from one function), turned to face the
        /// truck's bow and 2% fuller across, so the structure's own never fights it as it rises inside it. `Foot` is an empty
        /// at that part's base, and `Site` the structure's node on the part's axis: the foot lands on the site's (x, z) at the
        /// foot's own height. The part gives way to the structure's when the stage holding that one lands (`HideAt`, in build
        /// progress: stage_2 lands at 0.75).
        /// </summary>
        class Carry { public string Node, Foot, Site; public float HideAt; }

        static Carry CarryOf(string key) => key switch
        {
            "construction_truck" => new Carry { Node = "ct_mast", Foot = "ct_anchor", Site = "spinner", HideAt = 0.75f },
            _ => null,
        };

        /// <summary>The carried hand-off's phases (s): standing the part up, the structure's rise; lining up takes what the
        /// turn and the slide need, between the two bounds.</summary>
        public const float RaiseTime = 0.5f, RiseTime = 1.0f, MinLineUp = 0.25f, MaxLineUp = 0.7f;

        /// <summary>
        /// A carrying truck's act (HandOff); `site` is the structure's model (built, at build progress 0). The truck's
        /// `model`: (1) pivots and slides (0.25-0.7 s, its gear turning with it) until the carried part's foot is over that
        /// part's place in the structure, facing the structure's door side (-Z) with the part at its tail; (2) stands the
        /// part up off the tail (0.5 s); (3) sinks under the ground while the structure rises (PezEmerge build progress over
        /// 1 s), leaving the part planted on its spot until the structure's own identical part has risen into it. Everything
        /// that goes away is underground or inside its twin, so nothing pops. Effects go to `audience`.
        /// </summary>
        IEnumerator DeployInto(Transform model, Transform site, PezEmerge emerge, List<int> audience)
        {
            var siteNode = PezMotion.FindDeep(site, carry.Site);
            var lieRot = carried.localRotation;
            // The foot, standing, in the truck's model space (the hull at rest).
            Quaternion hullWas = hull ? hull.localRotation : Quaternion.identity;
            Vector3 hullWasAt = hull ? hull.localPosition : Vector3.zero;
            if (hull) { hull.localRotation = hullRest; hull.localPosition = hullPos; }
            carried.localRotation = Quaternion.identity;
            var foot = model.InverseTransformPoint(carriedFoot.position);
            float footY = carriedFoot.localPosition.y;      // its height in the part's own space, which is the structure's
            carried.localRotation = lieRot;
            if (hull) { hull.localRotation = hullWas; hull.localPosition = hullWasAt; }
            // Where it lands: the site node's axis at the foot's height, in the structure's space.
            var axis = siteNode ? site.InverseTransformPoint(siteNode.position) : Vector3.zero;
            var plant = site.TransformPoint(new Vector3(axis.x, footY, axis.z));
            var rot1 = site.rotation * Quaternion.Euler(0f, 180f, 0f);     // facing the structure's door side
            var p0 = model.position;
            var r0 = model.rotation;
            var p1 = plant - rot1 * Vector3.Scale(foot, model.lossyScale);
            p1.y = p0.y;

            // 1. Line up: pivot and slide onto the spot, the treads or wheels running with it (a pivot counter-runs them).
            float turn = Mathf.DeltaAngle(r0.eulerAngles.y, rot1.eulerAngles.y), dist = Vector3.Distance(Flat(p0), Flat(p1));
            float lineUp = Mathf.Clamp(Mathf.Max(Mathf.Abs(turn) / 360f, dist / 1.6f), MinLineUp, MaxLineUp);
            for (float t = 0f; t < lineUp; t += Time.deltaTime)
            {
                float k = Mathf.SmoothStep(0f, 1f, t / lineUp);
                var before = model.position;
                float yawBefore = model.eulerAngles.y, dt = Mathf.Max(Time.deltaTime, 1e-4f);
                model.SetPositionAndRotation(Vector3.Lerp(p0, p1, k), Quaternion.Slerp(r0, rot1, k));
                var d = Flat(model.position - before);
                float fwd = Vector3.Dot(d, model.forward), dyaw = Mathf.DeltaAngle(yawBefore, model.eulerAngles.y);
                drawnSpeed = fwd / dt; yawRate = dyaw / dt;
                Gear(fwd, dyaw * Mathf.Deg2Rad, dt);
                Steering(dt);
                SettleHull(dt);
                if (Mathf.Abs(yawRate) > 60f && (scrubT -= dt) <= 0f)
                {
                    scrubT = 0.12f;
                    float end = (Time.frameCount & 1) == 0 ? 1f : -1f;
                    var at = Flat(model.position + model.forward * length * 0.4f * end);
                    Fx.Audience = audience; Fx.TrackDust(at, model.right * Mathf.Sign(yawRate) * end, Mathf.Clamp(length, 0.6f, 1.6f)); Fx.Audience = null;
                }
                yield return null;
            }
            model.SetPositionAndRotation(p1, rot1);
            drawnSpeed = yawRate = 0f;

            // 2. Stand the part up off the tail: it swings up about its hinge and nods 3 deg past upright as it settles.
            for (float t = 0f; t < RaiseTime; t += Time.deltaTime)
            {
                float k = t / RaiseTime;
                float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(k / 0.8f)) + 0.035f * Mathf.Sin(Mathf.Clamp01((k - 0.8f) / 0.2f) * Mathf.PI);
                carried.localRotation = Quaternion.SlerpUnclamped(lieRot, Quaternion.identity, a);
                Steering(Time.deltaTime);
                SettleHull(Time.deltaTime);
                yield return null;
            }
            carried.localRotation = Quaternion.identity;
            if (hull) { hull.localRotation = hullRest; hull.localPosition = hullPos; }

            // 3. The structure rises round the part as the truck sinks away under it. When the part's foot reaches its spot it
            //    stays there (planted: a puff of dust each side), and gives way to the structure's own once that has risen into it.
            float sink = Height(model) + 0.05f;
            var start = model.position;
            bool planted = false, hid = false;
            for (float t = 0f; t < RiseTime; t += Time.deltaTime)
            {
                float k = t / RiseTime;
                emerge.SetBuildProgress(k);
                model.position = start + Vector3.down * sink * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(k / 0.55f));
                if (!planted && carriedFoot.position.y <= plant.y)
                {
                    planted = true;
                    carried.SetParent(model.parent, true);
                    carried.position += plant - carriedFoot.position;   // exactly on its spot
                    Fx.Audience = audience; Fx.SpadeDust(plant, -model.right); Fx.SpadeDust(plant, model.right); Fx.Audience = null;
                }
                if (!hid && k >= carry.HideAt) { ShowParts(carried, false); hid = true; }
                yield return null;
            }
            emerge.SetBuildProgress(1f);
            if (carried) Object.Destroy(carried.gameObject);
            Object.Destroy(model.gameObject);
        }

        static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// <summary>The hull's springs ease back to rest while it manoeuvres and stands its load up.</summary>
        void SettleHull(float dt)
        {
            if (!hull) return;
            float k = 1f - Mathf.Exp(-dt * 10f);
            hull.localRotation = Quaternion.Slerp(hull.localRotation, hullRest, k);
            hull.localPosition = Vector3.Lerp(hull.localPosition, hullPos, k);
        }

        /// <summary>How tall the truck stands, all but its load: how far it has to sink to be gone.</summary>
        float Height(Transform model)
        {
            float top = model.position.y;
            foreach (var r in model.GetComponentsInChildren<Renderer>())
                if (!r.transform.IsChildOf(carried)) top = Mathf.Max(top, r.bounds.max.y);
            return top - model.position.y;
        }

        /// <summary>Turn a carried part's renderers on or off.</summary>
        static void ShowParts(Transform t, bool on)
        {
            if (!t) return;
            foreach (var r in t.GetComponentsInChildren<Renderer>(true))
                r.enabled = on;
        }
    }
}
