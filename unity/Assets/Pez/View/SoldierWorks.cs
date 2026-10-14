using System.Collections;
using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// A candy trooper's upper body (build_models.candy_trooper: the rifleman and the soldiers built on it), from the sim:
    ///  - arms: authored lowered (the weapon at a low ready). The soldier brings it up level when it has something in
    ///    range to shoot (the sim's LastFiredAt: it's tracking a target), in 0.18 s, and holds it there 2.5 s after its last
    ///    shot before lowering it over 0.45 s. Each shot kicks it up (the muzzle climbs 9 degrees and settles in ~0.2 s)
    ///    while PezMotion slides the weapon back along the barrel.
    ///  - head: idle, it glances about (one side, back to the front, now and then the other), a slow look that eases in and
    ///    out; walking it mostly looks where it's going; with the weapon up it looks down the sights, cheek to the stock.
    ///    Being healed (<see cref="Tended"/>), it turns to look at its medic.
    /// Each kind of soldier has a small branch of its own (its <see cref="Kind"/> and <see cref="Style"/>):
    ///  - a shoulder launcher (the rocket soldier: a `rocket` node in the weapon, its loaded sweet, and a `breech` node):
    ///    each rocket (<see cref="Fired"/>) blows a backblast out of the breech and leaves an empty tube; the arms aim the
    ///    tube at what it shot (about level at the ground, up to 40 degrees at aircraft). The reload fits the cooldown: the
    ///    tube dips 22 degrees, the next sweet is pushed up the tube, and it comes back up on target. <see cref="Arm"/>
    ///    dresses the rocket in flight as that sweet. The tube kicks 6 degrees, not 9.
    ///  - a beam weapon (the laser trooper's `cell` and `lens`, M_E_Cyan) charges as LaserWorks does the tower's: idle the
    ///    cell breathes dim (30% on a 2 s sine) and the lens barely glows; in the last 0.25 s before the next shot is due
    ///    the cell brightens to full and the lens with it; on the shot the lens flashes and the cell drains in 0.2 s. A
    ///    charge that isn't fired bleeds away over 0.4 s. No kick: a beam has no recoil.
    ///  - the medic (unarmed, its heal spray hanging at its hip): tending a patient (the sim's Repair order, stopped in
    ///    range; the sim turns it to face the patient), it kneels and reaches out, both arms coming up MedicReach degrees
    ///    so the spray levels at the patient (the heal leaves its tip), its head on the patient. It gets up and lowers the
    ///    spray when the patient is whole or moves off.
    ///  - the engineer (his hard hat has a lamp): the lamp is lit while he's on a capture job, and within 1.4 tiles of the
    ///    building he brings his spanner up to the ready. The sim takes the building the tick he's within 0.6 of it and
    ///    removes him; WorldView then hands his model to <see cref="Capture"/> and <see cref="MoveIn"/>: he steps up to the
    ///    wall, knocks it three times, and the third blow is the capture (the building takes his colours as it lands).
    ///  - the sniper (a `bolt` and a scope `lens` on its rifle) is slower and heavier about it, and kneels to shoot: with a
    ///    target in range and standing still it goes down on one knee as it brings the rifle up, its eye to the scope, and
    ///    stands when it moves or 2.5 s after its last shot; the lens glows with the shot's readiness and glints once per
    ///    shot as the next comes ready; each shot kicks the rifle up 14 degrees, then the bolt cycles (the spent round's
    ///    foil wrapper flicks out), the head lifting off the scope while it works the bolt.
    ///  - the commando (a `charge` node in his hand, the C4 weapon) plants charges instead: see <see cref="Commando"/>.
    /// The kneels are Gait's (<see cref="Gait.Kneel"/>, in each unit's <see cref="Gait.KneelPose"/>): SoldierWorks only says
    /// when. The rest is elsewhere, one system per transform: Gait walks the legs, kneels and sways the hips (the idle
    /// weight shift), PezMotion yaws the torso (turret) to aim and recoils the weapon (barrel). Per frame: a few
    /// rotations (and a kind's glows), no allocations.
    /// </summary>
    public class SoldierWorks
    {
        enum Kind { Trooper, Rocket, Laser, Medic, Engineer, Sniper, Commando }

        /// <summary>How a kind of soldier handles its weapon.</summary>
        class Style
        {
            public float Up = 0.18f, Down = 0.45f, Hold = 2.5f, KickDeg = 9f, Settle = 14f, Sights = 7f, Cheek = 8f;
            /// <summary>The arms' raised pitch (degrees, negative is up): 0 levels a gun.</summary>
            public float Raised;
        }

        static readonly Style Trooper = new Style();
        static readonly Style Launcher = new Style { KickDeg = 6f };   // a recoilless tube shoves less than a rifle kicks
        static readonly Style Beam = new Style { KickDeg = 0f };       // a beam has no recoil
        static readonly Style Sniper = new Style { Up = 0.35f, Down = 0.5f, KickDeg = 14f, Settle = 8f, Sights = 15f, Cheek = 10f };
        static readonly Style Medic = new Style { Raised = -MedicReach, Sights = 6f, Cheek = 0f };
        static readonly Style Engineer = new Style { Raised = ReadyDeg, Cheek = 0f };

        // The kneels, in Gait (one each; the commando's legs have no knees).
        static readonly Gait.KneelPose MedicKneel = new Gait.KneelPose { Down = 0.2f, Up = 0.3f, Lean = 14f, Still = 0.7f, Thigh = 15f, Shin = 95f, KneeHigh = 0.036f };
        static readonly Gait.KneelPose SniperKneel = new Gait.KneelPose { Down = 0.45f, Up = 0.3f, Lean = 8f, Still = 0.75f, Thigh = 8f, Shin = 90f, KneeHigh = 0.044f };
        static readonly Gait.KneelPose CommandoKneel = new Gait.KneelPose { Down = 0.3f, Up = 0.25f, Lean = 18f, Still = 0.9f, Reach = 58f, Tuck = 0.8f, LeftDown = true };

        readonly Kind kind;
        readonly Style style;
        readonly Transform model, arms, head;
        readonly Quaternion armsRest, headRest;
        readonly Gait gait;
        readonly WorldView view;
        float raise, kick, seenShot = -99f;
        float yaw, pitch, roll, goal, nextLook;
        Vector3 tendedBy; float tendedUntil = -99f;

        SoldierWorks(Transform model, int seed, Gait gait, WorldView view)
        {
            this.model = model;
            this.gait = gait;
            this.view = view;
            arms = PezMotion.FindDeep(model, "arms");
            head = PezMotion.FindDeep(model, "head");
            if (arms) armsRest = arms.localRotation;
            if (head) headRest = head.localRotation;
            nextLook = Time.time + 1.5f + (seed * 0.618034f) % 1f * 3f;

            // Which soldier: by its kit's nodes (an invented unit on the same chassis gets the same branch).
            charge = PezMotion.FindDeep(model, "charge");
            round = PezMotion.FindDeep(model, "rocket");
            cell = Glow(model, "cell__M_E_Cyan");
            bolt = PezMotion.FindDeep(model, "bolt");
            var hat = PezMotion.FindDeep(model, "engineer_hardhat");
            kind = charge ? Kind.Commando : hat ? Kind.Engineer : round ? Kind.Rocket : cell ? Kind.Laser : bolt ? Kind.Sniper
                 : model.name == "medic" ? Kind.Medic : Kind.Trooper;
            style = kind switch
            {
                Kind.Rocket => Launcher, Kind.Laser => Beam, Kind.Sniper => Sniper, Kind.Medic => Medic, Kind.Engineer => Engineer,
                _ => Trooper,
            };
            if (gait != null)
                gait.Pose = kind switch { Kind.Medic => MedicKneel, Kind.Sniper => SniperKneel, Kind.Commando => CommandoKneel, _ => null };

            switch (kind)
            {
                case Kind.Commando:
                    chargeScale = charge.localScale;
                    break;
                case Kind.Rocket:
                    breech = PezMotion.FindDeep(model, "breech");
                    roundRest = round.localPosition;
                    break;
                case Kind.Laser:
                    beamLens = Glow(model, "lens__M_E_Cyan");
                    phase = seed * 0.37f;
                    break;
                case Kind.Engineer:
                    var l = PezMotion.FindDeep(hat, "lamp");
                    lamp = l ? l.GetComponentInChildren<Renderer>() : null;
                    if (lamp) foreach (var r in l.GetComponentsInChildren<Renderer>()) if (r.sharedMaterial != null && r.sharedMaterial.name.StartsWith("M_E_")) lamp = r;
                    turret = PezMotion.FindDeep(model, "turret");
                    hips = PezMotion.FindDeep(model, "hips");
                    legL = PezMotion.FindDeep(model, "leg_l");
                    legR = PezMotion.FindDeep(model, "leg_r");
                    muzzle = PezMotion.FindDeep(model, "muzzle");
                    break;
                case Kind.Sniper:
                    scope = PezMotion.FindDeep(model, "lens");
                    scopeR = scope ? scope.GetComponentInChildren<Renderer>() : null;
                    boltRest = bolt.localRotation;
                    boltPos = bolt.localPosition;
                    // The handle sticks out to one side of the bolt's axis (local Z): lift it toward +Y whichever side that
                    // is (the import mirrors x).
                    float side = 0f;
                    foreach (var mf in bolt.GetComponentsInChildren<MeshFilter>())
                        if (mf.sharedMesh) side += bolt.InverseTransformPoint(mf.transform.TransformPoint(mf.sharedMesh.bounds.center)).x;
                    boltUp = side >= 0f ? BoltDeg : -BoltDeg;
                    break;
            }
        }

        /// <summary>A trooper's works; a model without the nodes (older infantry) gets one that does nothing. `gait` is its
        /// walk (Models.Rig.Gait), told when to kneel; `view` is where it is drawn (who may see its effects, what it works
        /// on).</summary>
        public static SoldierWorks For(GameObject model, int seed, Gait gait = null, WorldView view = null) =>
            new SoldierWorks(model.transform, seed, gait, view);

        /// <summary>A medic is healing this soldier from `from` (world): it looks round at it for a moment (each heal pulse
        /// renews it), unless its weapon is up.</summary>
        public void Tended(Vector3 from) { tendedBy = from; tendedUntil = Time.time + 0.9f; }

        /// <param name="simTime">The sim's clock (World.Time), for the entity's LastFiredAt.</param>
        /// <param name="lastShot">When the view last played one of its shots (Time.time).</param>
        public void Tick(Entity e, float simTime, float lastShot, float dt)
        {
            if (dt <= 0f || Capturing) return;
            bool shot = lastShot > seenShot + 1e-4f;
            seenShot = lastShot;
            bool tracking = simTime - e.LastFiredAt < 0.6f;   // FireAt runs every tick it has a target in range
            if (kind == Kind.Commando && e.Def.Weapon != null && e.Def.Weapon.ArmTime > 0f) { Commando(e, tracking, shot, dt); return; }
            bool up = tracking || Time.time - lastShot < style.Hold || (e.Order == Order.Attack && !e.Moving);
            bool kneel = false;
            switch (kind)
            {
                case Kind.Medic:
                    // Tending: the sim stops it in range of its patient and heals every tick (World.UpdateRepair). A moment's
                    // settle first, so a medic that's only pausing on its way doesn't bob down and up.
                    tending = e.Order == Order.Repair && !e.Moving ? tending + dt : 0f;
                    up = kneel = tending > MedicSettle;
                    break;
                case Kind.Engineer:
                {
                    // On a capture job: the hat lamp on, and the spanner up at the ready as he nears the building.
                    bool job = e.Order == Order.Capture;
                    var t = job && view != null ? view.World.Get(e.TargetId) : null;
                    up = t != null && t.IsStructure && t.DistFrom(e.Pos) < ReadyRange;
                    lampK = Mathf.MoveTowards(lampK, job ? 1f : 0f, dt / 0.3f);
                    Light(lampK);
                    break;
                }
                case Kind.Sniper:
                    if (e.Moving && !tracking) up = false;   // it doesn't walk about with that rifle shouldered
                    kneel = up && !e.Moving;                 // it settles on one knee to shoot, and gets up to move
                    break;
            }
            if (gait != null) gait.Kneel = kneel;

            raise = Mathf.MoveTowards(raise, up ? 1f : 0f, dt / (up ? style.Up : style.Down));
            if (shot)
            {
                kick = Mathf.Min(kick + style.KickDeg, style.KickDeg * 1.4f);
                if (kind == Kind.Laser) { shotAt = Time.time; fired = beamCharge > 0.2f ? 1f : 0.6f; beamCharge = 0f; }
            }
            kick *= Mathf.Exp(-dt * style.Settle);
            float k = raise * raise * (3f - 2f * raise);

            // The weapon: raised (a launcher aimed at what it shot), the reload's dip, the kick, and the bolt's rock.
            float dip = kind == Kind.Rocket ? Reload(e, dt) : 0f;
            float work = kind == Kind.Sniper ? Bolt(e, shot, dt) : 0f;
            var raised = Quaternion.Euler(style.Raised, 0f, 0f);
            if (kind == Kind.Rocket)
            {
                if (raise <= 0f) elevGoal = 0f;  // lowered: the next fight starts level
                elev = Mathf.MoveTowards(elev, elevGoal, ElevRate * dt);
                raised = Quaternion.Euler(-elev, 0f, 0f);
            }
            if (arms) arms.localRotation = Quaternion.Slerp(armsRest, raised, k) * Quaternion.Euler(dip - kick + 3f * work, 0f, 0f);
            if (kind == Kind.Laser) Charge(e, tracking, dt);
            if (scopeR) Lens(e, tracking, shot, k, dt);

            // Weapon up: eyes down the sights (head tipped, cheek on the stock); off the scope while it works the bolt; a
            // launcher's eyes go up with the tube. The medic just watches its patient.
            float sights = k * (1f - 0.6f * work);
            float pitchGoal = kind == Kind.Rocket ? Mathf.Max(7f - elev * 0.5f, -12f) * k + kick * 0.3f + dip * 0.4f
                                                  : style.Sights * sights + kick * 0.3f;
            Head(e, k, pitchGoal, style.Cheek * sights, dt);
        }

        /// <summary>The head: glances about, damped by `focus` (0..1: looking where it works), tipped and rolled as asked.
        /// Being healed, it looks round at its medic instead (in the head's own frame, so it's right however the body turns).</summary>
        void Head(Entity e, float focus, float pitchGoal, float rollGoal, float dt)
        {
            if (!head) return;
            float now = Time.time;
            if (now >= nextLook)
            {
                // Mostly front, then a look to one side; walking, only small ones.
                bool side = Mathf.Abs(goal) < 1f && Random.value < 0.75f;
                float amp = e.Moving ? 18f : 42f;
                goal = side ? Random.Range(0.45f, 1f) * amp * (Random.value < 0.5f ? -1f : 1f) : 0f;
                nextLook = now + (side ? Random.Range(1.2f, 2.4f) : Random.Range(2f, 5f));
            }
            float g = Mathf.Lerp(goal, 0f, focus);
            if (now < tendedUntil && focus < 0.5f)
            {
                var to = head.parent.InverseTransformDirection(tendedBy - head.position);
                g = Mathf.Clamp(Mathf.Atan2(to.x, to.z) * Mathf.Rad2Deg, -70f, 70f);
            }
            float s = 1f - Mathf.Exp(-dt * 5f);
            yaw = Mathf.Lerp(yaw, g, s);
            pitch = Mathf.Lerp(pitch, pitchGoal, 1f - Mathf.Exp(-dt * 10f));
            roll = Mathf.Lerp(roll, rollGoal, 1f - Mathf.Exp(-dt * 10f));
            head.localRotation = headRest * Quaternion.Euler(pitch, yaw, roll);
        }

        static float Ease(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }

        static Renderer Glow(Transform model, string name)
        {
            var t = PezMotion.FindDeep(model, name);
            return t ? t.GetComponent<Renderer>() : null;
        }

        // ---------------------------------------------------------------- the medic

        /// <summary>How far the medic's arms come up to aim its spray (build_models.MEDIC_SPRAY_DOWN: it hangs that far below
        /// level), and how long it's stopped by its patient before it kneels.</summary>
        const float MedicReach = 65f, MedicSettle = 0.15f;
        float tending;

        // ---------------------------------------------------------------- the rocket soldier's launcher

        // The reload's dip, how far back up the tube the next sweet waits, the aim's limits and slew.
        const float DipDeg = 22f, Feed = 0.13f, ElevMin = -5f, ElevMax = 40f, ElevRate = 160f;
        readonly Transform round, breech;          // the loaded sweet and the tube's open back end
        readonly Vector3 roundRest;
        float elev, elevGoal, sinceRocket = 99f;

        /// <summary>A rocket left the tube (WorldView's fire event) for `target`, a world point: the backblast, an empty tube,
        /// the reload begins, and the arms aim at it. Does nothing for a soldier without a launcher.</summary>
        public void Fired(Vector3 target)
        {
            if (kind != Kind.Rocket || !arms) return;
            sinceRocket = 0f;
            round.localPosition = roundRest + Vector3.back * Feed;  // the next sweet, out of sight down the tube
            var d = target - arms.position;
            float flat = Mathf.Sqrt(d.x * d.x + d.z * d.z);
            elevGoal = Mathf.Clamp(Mathf.Atan2(d.y, Mathf.Max(flat, 0.01f)) * Mathf.Rad2Deg, ElevMin, ElevMax);
            if (breech) Fx.Backblast(breech.position, -breech.forward, round.lossyScale.y / 1.35f);  // 1 at the drawn size
        }

        /// <summary>The reload, timed to the weapon's cooldown (2.2 s for the rocket: done by 1.75 s): the tube dips over
        /// 0.4 s, the sweet rises up the tube into its mouth while it's down, and it comes back up. Returns the dip in degrees.</summary>
        float Reload(Entity e, float dt)
        {
            if (sinceRocket >= 99f) return 0f;
            sinceRocket += dt;
            float span = Mathf.Clamp((e.Def.Weapon != null ? e.Def.Weapon.Cooldown : 2.2f) / 2.2f, 0.25f, 1.5f);
            float t = sinceRocket / span;
            float feed = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.85f, 1.25f, t));
            round.localPosition = roundRest + Vector3.back * (Feed * (1f - feed));
            if (t >= 1.8f) { sinceRocket = 99f; round.localPosition = roundRest; return 0f; }
            return DipDeg * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.35f, 0.75f, t)) * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(1.35f, 1.75f, t)));
        }

        /// <summary>Dress a rocket in flight (WorldView's projectile, flying along its forward axis) as this soldier's
        /// sweet: a copy of the loaded one, the same size, nose first, just ahead of the projectile's exhaust glow.</summary>
        public void Arm(Transform shot)
        {
            if (kind != Kind.Rocket) return;
            float host = Mathf.Max(1e-4f, shot.localScale.y * 0.6f);
            shot.localScale = Vector3.one * host;          // the glow shrinks to the exhaust behind the sweet
            shot.rotation = round.rotation;                // leaving along the tube
            var copy = Object.Instantiate(round.gameObject, shot, false).transform;
            var ls = round.lossyScale;
            copy.localScale = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)) / host;
            copy.localRotation = Quaternion.identity;
            copy.localPosition = Vector3.forward * (0.05f / host);
            foreach (var t in copy.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = shot.gameObject.layer;
        }

        // ---------------------------------------------------------------- the laser trooper's beam rifle

        const float ChargeTime = 0.25f, Bleed = 0.4f, Drain = 0.2f, Flash = 0.3f, IdleGlow = 0.3f;
        readonly Renderer cell, beamLens;          // the charge cell and the lens (laser trooper only)
        readonly float phase;
        float shotAt = -99f, beamCharge, fired;

        /// <summary>A stand-in for a trooper that doesn't tick (the barracks' spat-out soldier, a case figurine): its beam
        /// weapon at rest, as the real one's will be when it takes over.</summary>
        public static void Rest(GameObject model)
        {
            var t = model.transform;
            Shade(Glow(t, "cell__M_E_Cyan"), IdleGlow);
            Shade(Glow(t, "lens__M_E_Cyan"), IdleGlow * 0.7f);
        }

        // The lamp body darkens with its light (as LaserWorks): an idle cell reads as dim cyan glass, not lit.
        static void Shade(Renderer r, float glow) { if (r) PezShade.Set(r, Mathf.Lerp(0.3f, 1f, Mathf.InverseLerp(0.15f, 1f, glow)), glow); }

        /// <summary>The beam weapon's charge, from the sim's cooldown: it reaches full as the next shot comes due.</summary>
        void Charge(Entity e, bool tracking, float dt)
        {
            bool due = tracking && e.Cooldown <= ChargeTime;
            beamCharge = Mathf.MoveTowards(beamCharge, due ? 1f : 0f, dt / (due ? ChargeTime : Bleed));
            float since = Time.time - shotAt;
            float drain = since < Drain ? fired * (1f - since / Drain) : 0f;
            float flash = since < Flash ? 1f - since / Flash : 0f;
            float breath = IdleGlow + 0.06f * Mathf.Sin((Time.time / 2f + phase) * 6.2831853f);
            float c = Mathf.Max(beamCharge * beamCharge * (3f - 2f * beamCharge), drain);
            Shade(cell, Mathf.Lerp(breath, 1.8f, c));
            Shade(beamLens, Mathf.Lerp(IdleGlow * 0.7f, 1.2f, beamCharge) + 2.6f * flash * flash);
        }

        // ---------------------------------------------------------------- the sniper's scope and bolt

        const float BoltAt = 0.5f, BoltLift = 0.1f, BoltRun = 0.13f, BoltDeg = 45f, BoltTravel = 0.032f;
        const float GlintBefore = 0.35f;   // seconds of cooldown left when the scope glints
        readonly Transform bolt, scope;
        readonly Renderer scopeR;
        readonly Quaternion boltRest;
        readonly Vector3 boltPos;
        readonly float boltUp;             // the bolt's lift about its axis, signed so the handle comes up
        float boltT = -1f, glow = -1f, glint;
        bool glinted, ejected = true;

        /// <summary>Working the bolt after the shot: lift, back, home, down. Returns how hard it's at it (0..1).</summary>
        float Bolt(Entity e, bool shot, float dt)
        {
            float work = 0f;
            if (shot) { boltT = 0f; ejected = false; }
            if (boltT < 0f) return 0f;
            boltT += dt;
            float t = boltT - BoltAt, lift = 0f, back = 0f;
            if (t > 0f)
            {
                lift = Ease(t / BoltLift) * (1f - Ease((t - BoltLift - 2f * BoltRun) / BoltLift));
                back = Ease((t - BoltLift) / BoltRun) * (1f - Ease((t - BoltLift - BoltRun) / BoltRun));
                work = Mathf.Clamp01(t / BoltLift) * (1f - Mathf.Clamp01((t - BoltLift - 2f * BoltRun) / BoltLift));
                if (!ejected && t > BoltLift + BoltRun * 0.8f) { ejected = true; Eject(e); }
            }
            bolt.localRotation = boltRest * Quaternion.Euler(0f, 0f, boltUp * lift);
            bolt.localPosition = boltPos + Vector3.back * BoltTravel * back;
            if (t > 2f * BoltLift + 2f * BoltRun) { boltT = -1f; bolt.localRotation = boltRest; bolt.localPosition = boltPos; }
            return work;
        }

        /// <summary>The scope's lens shows the next shot coming: dim just after one, full when the rifle is ready, and a
        /// glint off it (once a shot) as it comes ready with a target in range.</summary>
        void Lens(Entity e, bool tracking, bool shot, float k, float dt)
        {
            float cd = e.Def.Weapon != null ? Mathf.Max(0.1f, e.Def.Weapon.Cooldown) : 1f;
            float ready = 1f - Mathf.Clamp01(e.Cooldown / cd);
            if (shot) glinted = false;
            if (!glinted && tracking && k > 0.6f && e.Cooldown <= GlintBefore)
            {
                glinted = true;
                glint = 1f;
                Fx.Audience = view != null ? view.AudienceFor(e) : null;
                Fx.ScopeGlint(scope.position, scope.forward);
                Fx.Audience = null;
            }
            glint = Mathf.MoveTowards(glint, 0f, dt / 0.3f);
            float g = Mathf.Lerp(0.2f, 1f, ready * ready) + 2.5f * glint;
            if (Mathf.Abs(g - glow) > 0.01f) { PezShade.Set(scopeR, 1f, g); glow = g; }
        }

        /// <summary>The spent round's foil wrapper flicks out of the bolt's side.</summary>
        void Eject(Entity e)
        {
            var side = bolt.TransformDirection(new Vector3(boltUp > 0f ? 1f : -1f, 0f, 0f));
            Fx.Audience = view != null ? view.AudienceFor(e) : null;
            Fx.SpentWrapper(bolt.position, side);
            Fx.Audience = null;
        }

        // ---------------------------------------------------------------- the engineer's capture

        // The spanner's ready pitch (above level), how near the building he raises it, and his act's beats.
        const float ReadyDeg = -20f, ReadyRange = 1.4f;
        const float WindDeg = -38f, BlowDeg = 30f, BlowTime = 0.3f, Blows = 3, After = 0.25f, InTime = 0.35f;
        const float Reach = 0.3f;       // pivot to the front of the spanner's jaw at the blow, in model units (0.4 at the 1.35 draw scale)
        readonly Renderer lamp;
        readonly Transform turret, hips, legL, legR, muzzle;
        float lampK = 1f, lampShown = -1f;
        Vector3 workAt, center, wallDir;
        Color team;
        float stepTime;
        int strides;
        List<int> audience;
        /// <summary>The engineer's capture landed and his act is planned (<see cref="Capture"/>): WorldView plays it as he retires.</summary>
        public bool Capturing { get; private set; }

        void Light(float g)
        {
            if (!lamp || Mathf.Abs(g - lampShown) < 0.01f) return;
            PezShade.Set(lamp, Mathf.Lerp(0.45f, 1f, g), g * 1.3f);
            lampShown = g;
        }

        /// <summary>
        /// The engineer's capture has landed in the sim (and he's gone from it): plan his act at the building and say how
        /// long until its last blow, which is when the building should take its new colours.
        /// </summary>
        /// <param name="building">The captured building's model (its tall parts are the walls he knocks on).</param>
        /// <param name="teamColor">His team's colour, for the capture's beam.</param>
        /// <param name="audience">Who sees effects there (WorldView.AudienceFor), copied.</param>
        public float Capture(GameObject building, Color teamColor, List<int> audience)
        {
            if (kind != Kind.Engineer || !turret || !hips || !legL || !legR || !arms || building == null) return 0f;
            center = building.transform.position;
            team = teamColor;
            this.audience = audience;
            // The walls: the box round the building's tall parts (pads and plinths left out).
            var walls = new Bounds(center, Vector3.zero);
            bool any = false;
            foreach (var r in building.GetComponentsInChildren<Renderer>())
            {
                var b = r.bounds;
                if (b.max.y < 0.2f || b.size.y < 0.1f) continue;
                if (!any) { walls = b; any = true; } else walls.Encapsulate(b);
            }
            var p = model.position;
            var near = new Vector3(Mathf.Clamp(p.x, walls.min.x, walls.max.x), p.y, Mathf.Clamp(p.z, walls.min.z, walls.max.z));
            var away = p - near; away.y = 0f;
            float d = away.magnitude;
            if (d < 1e-3f) { away = p - center; away.y = 0f; d = away.magnitude; }
            wallDir = d > 1e-3f ? -away / d : model.forward;
            // He steps up to the wall: close enough for the spanner's jaw to land on it (at most 0.6 tiles).
            float reach = Reach * model.lossyScale.y;
            float step = Mathf.Clamp(d - reach, 0f, 0.6f);
            workAt = p + wallDir * step;
            stepTime = step > 0.02f ? Mathf.Max(0.25f, step / 0.9f) : 0f;
            strides = Mathf.Max(1, Mathf.RoundToInt(step / 0.36f));
            Capturing = true;
            return stepTime + Blows * BlowTime;
        }

        /// <summary>The engineer's act, once he's left the sim (run on his model, detached from its view): see the class notes.
        /// It owns every node of the model now (Gait and PezMotion have stopped) and destroys it at the end.</summary>
        public IEnumerator MoveIn()
        {
            var motion = model.GetComponent<PezMotion>();
            if (motion) motion.enabled = false;
            var from = model.position;
            var rot0 = model.rotation;
            var face = Quaternion.LookRotation(wallDir, Vector3.up);
            Quaternion l0 = legL.localRotation, r0 = legR.localRotation, h0 = hips.localRotation, t0 = turret.localRotation;
            Quaternion hd0 = head ? head.localRotation : Quaternion.identity;
            Vector3 ls0 = legL.localScale, rs0 = legR.localScale, hp0 = hips.localPosition;
            var hipsRest = new Vector3(0f, legL.localPosition.y, 0f);
            float a0 = Pitch(arms.localRotation);
            float legLen = legL.localPosition.y;
            // Gait had the body down by L(1 - cos a) for the legs' swing: the ground is that much below his pivot's rest.
            float ground = from.y + legLen * (1f - Mathf.Cos(Pitch(l0) * Mathf.Deg2Rad)) * model.lossyScale.y;
            float last = stepTime + Blows * BlowTime;            // the capture's blow
            float inAt = last + After, end = inAt + InTime;
            var scale0 = model.localScale;
            int hits = 0;
            for (float t = 0f; t < end; t += Time.deltaTime)
            {
                if (model == null) yield break;
                // Ease out of whatever Gait and Tick left (0.12 s), then the act.
                float ease = Mathf.SmoothStep(0f, 1f, t / 0.12f);

                // Feet: the step up to the wall (a stride or two), still while he works, a stride into the building.
                float u = stepTime > 0f ? Mathf.Clamp01(t / stepTime) : 1f;
                float v = Mathf.Clamp01((t - inAt) / InTime);
                float swing = (stepTime > 0f && u < 1f ? 22f * Mathf.Sin(u * strides * 2f * Mathf.PI) : 0f)
                            + (v > 0f ? 18f * Mathf.Sin(v * 2f * Mathf.PI) : 0f);
                legL.localRotation = Quaternion.Slerp(l0, Quaternion.Euler(-swing, 0f, 0f), ease);
                legR.localRotation = Quaternion.Slerp(r0, Quaternion.Euler(swing, 0f, 0f), ease);
                legL.localScale = Vector3.Lerp(ls0, Vector3.one, ease);
                legR.localScale = Vector3.Lerp(rs0, Vector3.one, ease);
                hips.localPosition = Vector3.Lerp(hp0, hipsRest, ease);
                hips.localRotation = Quaternion.Slerp(h0, Quaternion.identity, ease);
                // Rigid legs keep the soles down if the body drops by L(1 - cos a), as Gait does.
                float drop = legLen * (1f - Mathf.Cos(Pitch(legL.localRotation) * Mathf.Deg2Rad)) * scale0.y * (1f - v * v);

                // Where he is: turned to the wall (0.15 s), stepping up to it, then into the building as he shrinks away.
                var at = Vector3.Lerp(from, workAt, u * u * (3f - 2f * u));
                if (v > 0f) { var inward = center - workAt; inward.y = 0f; at += inward.normalized * (0.35f * v); }
                at.y = ground - drop;
                model.position = at;
                model.rotation = Quaternion.Slerp(rot0, face, Mathf.SmoothStep(0f, 1f, t / 0.15f));
                model.localScale = scale0 * (1f - v * v);

                // The spanner: wound up and brought down three times; the third lands the capture. Then back to the ready.
                float arm = ArmAt(t, a0);
                arms.localRotation = Quaternion.Euler(arm, 0f, 0f);
                // The torso leans into each blow and back on the wind-up; the head looks up at the spanner as it goes up
                // (which also tips the brim clear of it) and watches the jaw land.
                float lean = Mathf.Lerp(-5f, 16f, Mathf.InverseLerp(WindDeg, BlowDeg, arm));   // about 0 at the ready
                turret.localRotation = Quaternion.Slerp(t0, Quaternion.Euler(lean, 0f, 0f), ease);
                if (head) head.localRotation = Quaternion.Slerp(hd0, headRest * Quaternion.Euler(lean < 0f ? lean * 2f : lean * 0.4f, 0f, 0f), ease);

                while (hits < Blows && t >= stepTime + (hits + 1) * BlowTime)
                {
                    hits++;
                    Fx.Audience = audience;
                    if (muzzle) { Fx.BulletImpact(muzzle.position); Fx.MuzzleFlash(muzzle.position, hits < Blows ? 0.1f : 0.16f); } // sparks and a flash off the jaw
                    if (hits == Blows)
                    {
                        // The capture lands (WorldView's "captured" beam and flash, played on his blow).
                        Fx.Beam(new Vector3(center.x, 0.2f, center.z), new Vector3(center.x, 4f, center.z), team, 0.25f);
                        Fx.MuzzleFlash(new Vector3(center.x, 0.6f, center.z), 0.4f);
                    }
                    Fx.Audience = null;
                }
                yield return null;
            }
            if (model != null) Object.Destroy(model.gameObject);
        }

        /// <summary>The arms' pitch (degrees, + is down) at time t of the act: the blows' wind-ups and strikes, from a0.</summary>
        float ArmAt(float t, float a0)
        {
            float first = stepTime;                               // the first wind-up starts as he arrives at the wall
            if (t < first) return Mathf.LerpAngle(a0, ReadyDeg, Mathf.SmoothStep(0f, 1f, t / Mathf.Max(0.1f, first)));
            float last = stepTime + Blows * BlowTime;
            if (t >= last)
            {
                // The rebound: back up to the ready, eased out.
                float r = Mathf.Clamp01((t - last) / 0.25f);
                return Mathf.Lerp(BlowDeg, ReadyDeg, 1f - (1f - r) * (1f - r));
            }
            int i = Mathf.FloorToInt((t - first) / BlowTime);
            float s = (t - first) / BlowTime - i;
            float prev = i > 0 ? BlowDeg : first > 0f ? ReadyDeg : a0;
            if (s < 0.62f) { float w = s / 0.62f; return Mathf.Lerp(prev, WindDeg, 1f - (1f - w) * (1f - w)); }  // wind up, easing out
            float f = (s - 0.62f) / 0.38f;
            return Mathf.Lerp(WindDeg, BlowDeg, f * f);                                                          // and down, accelerating
        }

        static float Pitch(Quaternion q)
        {
            float x = q.eulerAngles.x;
            return x > 180f ? x - 360f : x;
        }

        // ---------------------------------------------------------------- the commando's C4

        // The sim (Defs C4, World.FireAt): a charge has to be planted. In range of its target (1 tile from a building's
        // footprint) and standing still, the commando works it for the weapon's ArmTime (1.5 s: the sim's Arming) before the
        // first charge goes off: the `shot` event IS the blast (900 damage, no projectile). Then every 5 s (its cooldown)
        // while he stays on it. Walking off or switching target starts the arming again. So every charge is shown planted:
        //   kneel -> (a fresh charge in hand) reach back -> lob it underhand onto the target (C4Charge: it sticks and blinks
        //   faster as the time to the bang runs out) -> stay down until the bang (he ducks his head) -> draw the next.
        // A charge that would be lobbed too late to land before its bang (he arrived with the arming nearly done) goes off
        // where it would have stuck, and leaves his hand.
        const float ReachBack = 0.32f, Lob = 0.2f, Follow = 0.45f;   // the plant's beats (s)
        const float Lands = ReachBack + Lob * 0.66f + 0.42f;         // from the reach to the charge stuck on the wall (C4Charge.Flight)
        const float BackDeg = 55f, ThrowDeg = -80f;                  // the arms: back to the satchel, the underhand swing
        readonly Transform charge;
        readonly Vector3 chargeScale;
        C4Charge planted;
        int c4Target = -1;
        float hand = 1f, plantT = -1f, duck, armsDeg;
        bool lobbed;

        /// <summary>The commando at work: see the notes above.</summary>
        void Commando(Entity e, bool tracking, bool shot, float dt)
        {
            var w = e.Def.Weapon;
            bool down = tracking && !e.Moving;
            if (shot) { hand = 0f; lobbed = false; plantT = -1f; duck = 1f; }   // the charge went (from his hand, or off the wall)
            int target = e.ArmingTarget;
            if (!down || target != c4Target) lobbed = false;                   // off the job (or onto another): the next is a fresh plant
            c4Target = target;
            // A charge on the wall that won't be blown (he's off, or after something else): it fizzles.
            if (planted != null && (!down || planted.Target != c4Target)) { planted.Fizzle(); planted = null; }

            // The bang to come: the arming still to do or the cooldown, whichever is longer, out of the one that's running.
            float arming = Mathf.Max(0f, w.ArmTime - e.Arming);
            float left = Mathf.Max(arming, e.Cooldown);
            float span = arming >= e.Cooldown ? w.ArmTime : w.Cooldown;

            // Plant the next one: once he's going down, the last blast has cleared, and there's time for it to land.
            if (down && !lobbed && plantT < 0f && planted == null && (gait == null || gait.Kneeling > 0.5f) && left > Lands + 0.05f &&
                e.Cooldown < w.Cooldown - 0.3f && view != null && view.Views.TryGetValue(c4Target, out var tv) && !tv.E.Dead) plantT = 0f;
            if (plantT >= 0f)
            {
                plantT += dt;
                float t = plantT;
                // Back to the satchel (a fresh charge comes out of it halfway), the underhand swing, the follow-through.
                if (t < ReachBack) armsDeg = BackDeg * Mathf.SmoothStep(0f, 1f, t / ReachBack);
                else if (t < ReachBack + Lob) { float u = (t - ReachBack) / Lob; armsDeg = Mathf.Lerp(BackDeg, ThrowDeg, u * u * (3f - 2f * u)); }
                else if (t < ReachBack + Lob + Follow) armsDeg = ThrowDeg * (1f - Mathf.SmoothStep(0f, 1f, (t - ReachBack - Lob) / Follow));
                else { plantT = -1f; armsDeg = 0f; }
                if (!lobbed && t > ReachBack * 0.5f) hand = Mathf.MoveTowards(hand, 1f, dt / 0.1f);
                // It lets go two thirds of the way up the swing (on time even if a slow frame skipped past it).
                if (!lobbed && t >= ReachBack + Lob * 0.66f) Release();
            }
            else
            {
                // Not planting: up and about, he takes a charge out again (after it went, or once he's moving on).
                if (!down || !lobbed && hand < 1f && left <= Lands) hand = Mathf.MoveTowards(hand, 1f, dt / 0.35f);
                armsDeg = Mathf.Lerp(armsDeg, 0f, 1f - Mathf.Exp(-dt * 10f));
            }
            if (planted != null) planted.Feed(span > 0f ? left / span : 0f);

            // Down on one knee while he works the target; up to walk.
            if (gait != null) gait.Kneel = down;
            if (arms) arms.localRotation = armsRest * Quaternion.Euler(armsDeg, 0f, 0f);
            float hs = hand * hand * (3f - 2f * hand);
            if (charge) charge.localScale = chargeScale * hs;
            // Eyes on the target; at the bang he ducks his head.
            duck = Mathf.MoveTowards(duck, 0f, dt / 0.6f);
            Head(e, down ? 1f : 0f, (down ? 6f : 0f) + 22f * duck * (2f - duck), 0f, dt);
        }

        void Release()
        {
            lobbed = true;
            if (charge) charge.localScale = chargeScale;   // whole as it leaves the hand
            if (view != null && view.Views.TryGetValue(c4Target, out var tv) && Stick(tv, out var at, out var normal))
                planted = C4Charge.Throw(charge, tv.Rig.Root, c4Target, at, normal);   // a copy, from his hand as it is
            hand = 0f;
        }

        /// <summary>Where a charge thrown from here sticks on a target, and the surface's normal there: the point of the
        /// target's own surface nearest him that the player's camera can see (a charge on the far side of a building is
        /// no use to anyone), at least waist high, so clear of its plinth. Found by casting the camera's rays at points along
        /// the line from him into the target and testing them against its meshes, triangle by triangle (a few thousand
        /// tests, once a plant; poles and cloth don't count). Without a camera or a hit: where that line first meets one of its parts' bounds.</summary>
        bool Stick(WorldView.EV tv, out Vector3 at, out Vector3 normal)
        {
            var src = tv.Rig.Model != null ? tv.Rig.Model.transform : tv.Rig.Root;
            var me = charge.position;
            var mid = tv.Rig.Root.position;
            float ground = mid.y, h = ground + 0.25f;
            var dir = mid - me; dir.y = 0f;
            float len = dir.magnitude;
            at = mid + Vector3.up * 0.25f; normal = -dir;
            if (len < 1e-3f) return false;
            dir /= len;
            normal = -dir;
            var cam = Camera.main;
            float bestD = float.MaxValue;
            if (cam != null)
            {
                var eye = cam.transform.position;
                for (int i = 0; i < 9; i++)
                {
                    var q = me + dir * (len * (0.3f + 0.7f * i / 8f)); q.y = h;
                    var ray = new Ray(eye, (q - eye).normalized);
                    if (!RayModel(src, ray, out var p, out var n) || p.y < ground + 0.18f || n.y < -0.3f) continue;
                    float d = new Vector2(p.x - me.x, p.z - me.z).magnitude;
                    if (d < bestD) { bestD = d; at = p; normal = n; }
                }
            }
            if (bestD < float.MaxValue) { at += normal * 0.015f; return true; }
            // No camera, or it saw none of it: the line from him meets the first part's bounds, at waist height.
            var level = new Ray(new Vector3(me.x, h, me.z), dir);
            float best = len;
            foreach (var r in src.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled || r.GetComponentInParent<C4Charge>() != null) continue;
                var b = r.bounds;
                if (b.max.y < h || b.min.y > h || Mathf.Min(b.size.x, b.size.z) < 0.06f) continue;
                if (b.IntersectRay(level, out float d) && d > 0.05f && d < best) best = d;
            }
            at = level.GetPoint(Mathf.Max(0f, best - 0.02f));
            return true;
        }

        static readonly Dictionary<Mesh, (Vector3[] v, int[] t)> meshData = new Dictionary<Mesh, (Vector3[], int[])>();

        /// <summary>The nearest point where a ray meets a model's (readable) meshes, and the face's normal turned to the ray.</summary>
        static bool RayModel(Transform model, Ray ray, out Vector3 hit, out Vector3 normal)
        {
            hit = normal = Vector3.zero;
            float best = float.MaxValue;
            foreach (var mf in model.GetComponentsInChildren<MeshFilter>())
            {
                var mesh = mf.sharedMesh;
                var r = mf.GetComponent<MeshRenderer>();
                if (mesh == null || r == null || !r.enabled || !mesh.isReadable || mf.GetComponentInParent<C4Charge>() != null) continue;
                var bb = r.bounds;
                if (Mathf.Min(bb.size.x, bb.size.z) < 0.06f || !bb.IntersectRay(ray)) continue;   // not on a pole or a flag
                if (!meshData.TryGetValue(mesh, out var md)) meshData[mesh] = md = (mesh.vertices, mesh.triangles);
                var tf = mf.transform;
                var o = tf.InverseTransformPoint(ray.origin);
                var d = tf.InverseTransformVector(ray.direction);
                var v = md.v; var t = md.t;
                for (int i = 0; i < t.Length; i += 3)
                {
                    Vector3 a = v[t[i]], e1 = v[t[i + 1]] - a, e2 = v[t[i + 2]] - a;
                    var pv = Vector3.Cross(d, e2);
                    float det = Vector3.Dot(e1, pv);
                    if (det > -1e-9f && det < 1e-9f) continue;
                    float inv = 1f / det;
                    var tv = o - a;
                    float u = Vector3.Dot(tv, pv) * inv;
                    if (u < 0f || u > 1f) continue;
                    var qv = Vector3.Cross(tv, e1);
                    float w = Vector3.Dot(d, qv) * inv;
                    if (w < 0f || u + w > 1f) continue;
                    float s = Vector3.Dot(e2, qv) * inv;
                    if (s <= 0f) continue;
                    var p = tf.TransformPoint(o + d * s);
                    float dist = (p - ray.origin).sqrMagnitude;
                    if (dist >= best) continue;
                    best = dist;
                    hit = p;
                    var n = tf.TransformDirection(Vector3.Cross(e1, e2)).normalized;
                    normal = Vector3.Dot(n, ray.direction) > 0f ? -n : n;
                }
            }
            return best < float.MaxValue;
        }

        /// <summary>The sim's C4 `shot` on a target: where it goes off. The charge he planted on it if there is one (gone
        /// now), else where he'd have stuck it (and the one in his hand goes).</summary>
        public Vector3 Detonate(int target, Vector3 fallback)
        {
            if (kind != Kind.Commando) return fallback;
            if (planted != null)
            {
                bool same = planted.Target == target;
                var p = planted.Position;
                Object.Destroy(planted.gameObject);
                planted = null;
                if (same) return p;
            }
            if (charge && view != null && view.Views.TryGetValue(target, out var tv) && Stick(tv, out var at, out _)) return at;
            return fallback;
        }
    }
}
