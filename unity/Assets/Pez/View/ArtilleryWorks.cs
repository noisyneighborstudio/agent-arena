using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The artillery family's act (artillery and long-range artillery; MOTION.md), a helper VehicleWorks owns and ticks.
    /// It drives only its own nodes: `cradle` (the team head, hinged at its back bottom edge: tipping it back lays the gun
    /// in its mouth, as a Pez dispenser's head tips back), `spade_l` / `spade_r` and `arty_round` (the candy round on the
    /// dispenser's stem top, which the hollow head covers shut and shows tipped back). VehicleWorks keeps the hull and the
    /// running gear (it squats the body by Deploy); PezMotion keeps the turret's yaw and the barrel's recoil and ram.
    ///  - Travel (the model's rest pose): the head shut, the gun level in the bow's travel lock, the spades folded back
    ///    flat behind. It travels like that until it's about to stop to fire (its target within Speed x 0.6 tiles of 95% of
    ///    its range), when the head tips back to lay the gun.
    ///  - Laying: the gun's elevation is the shell's real launch angle. Across the ground the shell flies at the weapon's
    ///    speed (8 tiles/s), and its arc (WorldView.SyncProjectiles) falls under one gravity, so the apex over D tiles is
    ///    Gravity D^2 / (8 v^2) and the launch angle is atan(Gravity D / (2 v^2) + rise / D): about 45 deg at 8 tiles,
    ///    54 at 11, 56 at 12. The head tips at 90 deg/s and settles at 70.
    ///  - Deploy, stopped and engaged: the spades swing down 55 deg (0.25 s, ease-in), bite (dust off each blade) and the
    ///    body squats 0.04 over 0.4 s. Each reload the barrel rams (PezMotion.Ram, 0.4 s, ending before the earliest
    ///    shot) and the round in the stem's cradle is pushed up into the breech (Round); after the shot the next one
    ///    rises out of the stem. Moving again, the spades fold (0.25 s) and, once the head faces forward, the gun lowers onto the lock.
    ///  - The head only turns with the gun lifted at least ClearElev (it clears the deck, the rack and the spades all
    ///    round from there; WorldView.Aim waits on Clear), and below it the gun only lies forward.
    ///  - A shot never waits: Ready snaps the gun to its lay (and plants the spades, if stopped) the frame it fires.
    /// </summary>
    public class ArtilleryWorks
    {
        /// <summary>The shell's gravity (tiles/s^2) in the view's arc: one value for the arc and the gun's lay.</summary>
        public const float Gravity = 16f;
        /// <summary>The lowest elevation (deg) at which the laid gun clears everything as the head turns all round.</summary>
        public const float ClearElev = 14f;
        const float MaxElev = 62f, SpadeSwing = 55f, Rise = 90f, Settle = 70f, MuzzleY = 0.6f, TargetY = 0.3f;

        readonly Transform cradle, spadeL, spadeR, turret, round, barrel;
        readonly Vector3 roundRest;
        readonly Quaternion roundRestRot;
        // The round: in the stem's cradle, being rammed up into the breech, chambered (inside the tube), spent (still
        // inside, out of sight, until the recoil is over) or rising out of the stem into the cradle (the next candy).
        enum Load { Cradle, Ramming, Chambered, Spent, Rising }
        Load load;
        float loadK, shotAt = -99f;
        const float RoundLen = 0.11f, RamUp = 0.3f, RiseTime = 0.35f, SpentFor = 0.8f;
        readonly Quaternion cradleRest, spadeRestL, spadeRestR, turretRest;
        readonly Vector3 tipL, tipR;
        float elev, lay = 45f;
        bool rammed;

        /// <summary>Dug in: 0 travelling .. 1 deployed (VehicleWorks squats the body 0.04 by it).</summary>
        public float Deploy { get; private set; }
        /// <summary>The head may turn: the gun is lifted clear of the deck.</summary>
        public bool Clear => elev >= ClearElev - 0.01f;

        ArtilleryWorks(Transform model, Transform cradle)
        {
            this.cradle = cradle;
            cradleRest = cradle.localRotation;
            turret = PezMotion.FindDeep(model, "turret");
            if (turret) turretRest = turret.localRotation;
            barrel = PezMotion.FindDeep(model, "barrel");
            round = PezMotion.FindDeep(model, "arty_round");
            if (round) { roundRest = round.localPosition; roundRestRot = round.localRotation; }
            spadeL = PezMotion.FindDeep(model, "spade_l");
            spadeR = PezMotion.FindDeep(model, "spade_r");
            if (spadeL) { spadeRestL = spadeL.localRotation; tipL = Tip(spadeL); }
            if (spadeR) { spadeRestR = spadeR.localRotation; tipR = Tip(spadeR); }
        }

        /// <summary>The act for a model with a `cradle` (the artillery family), or null.</summary>
        public static ArtilleryWorks For(Transform model) =>
            model != null && PezMotion.FindDeep(model, "cradle") is Transform c ? new ArtilleryWorks(model, c) : null;

        /// <summary>The launch angle (deg) for a shell flying D tiles across the ground at `speed`, rising `rise`.</summary>
        public static float LayFor(float dist, float speed, float rise)
        {
            dist = Mathf.Max(dist, 0.5f);
            return Mathf.Clamp(Mathf.Atan(Gravity * dist / (2f * speed * speed) + rise / dist) * Mathf.Rad2Deg, ClearElev, MaxElev);
        }

        /// <summary>The shell's apex over its flight of `dist` tiles at `speed` (WorldView's arc).</summary>
        public static float Apex(float dist, float speed) => Gravity * dist * dist / (8f * speed * speed);

        public void Tick(WorldView view, WorldView.EV v, float dt)
        {
            var e = v.E;
            bool attacking = e.Order == Order.Attack || (e.Order == Order.AttackMove && e.TargetId != 0);
            bool engaged = Time.time - v.LastFire < 3f || attacking;
            bool deploy = !e.Moving && engaged;
            float was = Deploy;
            Deploy = Mathf.MoveTowards(Deploy, deploy ? 1f : 0f, dt / (deploy ? 0.4f : 0.25f));
            // Spades: they slam down (ease-in) in the first 0.25 s and bite; they fold smoothly.
            float k0 = Mathf.Clamp01(was / 0.6f), k = Mathf.Clamp01(Deploy / 0.6f);
            Spades(deploy ? k * k : Mathf.SmoothStep(0f, 1f, k));
            if (deploy && k0 < 1f && k >= 1f) Bite(v.Rig.Root);
            // The lay: the launch angle to its target, held after it (the next shot most likely goes the same way).
            var t = attacking && e.Def.Weapon != null ? view.World.Get(e.TargetId) : null;
            if (t != null) lay = LayFor(Vec2.Dist(e.Pos, t.Center), e.Def.Weapon.ProjectileSpeed, TargetY - MuzzleY);
            bool raise = deploy;
            if (!raise && e.Moving && t != null) raise = Vec2.Dist(e.Pos, t.Center) - e.Def.Weapon.Range * 0.95f < e.Def.Speed * 0.6f;
            bool forward = turret == null || Quaternion.Angle(turret.localRotation, turretRest) < 2f;
            float goal = raise ? lay : forward ? 0f : Mathf.Max(elev, ClearElev);
            elev = Mathf.MoveTowards(elev, goal, dt * (goal > elev ? Rise : Settle));
            Head();
            if (engaged && e.Cooldown > 0.2f && e.Cooldown < 0.6f && !rammed)
            {
                v.Rig.Motion.Ram();
                rammed = true;
                if (load == Load.Cradle) { load = Load.Ramming; loadK = 0f; }
            }
            Round(dt);
        }

        /// <summary>It fired: the next reload rams again.</summary>
        public void Shot()
        {
            rammed = false;
            shotAt = Time.time;
            // A first shot can precede the reload animation. The authoritative shot chambers the candy immediately;
            // it never sweeps a waiting round through a barrel that is already recoiling.
            load = Load.Spent;
            if (round && barrel) round.SetPositionAndRotation(Chamber(), barrel.rotation);
        }

        /// <summary>The candy round: the reload ram pushes it from the stem's cradle up into the breech (0.3 s); it stays
        /// chambered, inside the tube, until the shot; 0.8 s after it (the recoil over) the next one rises out of the stem
        /// into the cradle (0.35 s), like a dispenser's next candy.</summary>
        void Round(float dt)
        {
            if (!round || !barrel) return;
            switch (load)
            {
                case Load.Cradle:
                    round.localPosition = roundRest; round.localRotation = roundRestRot;
                    break;
                case Load.Ramming:
                    loadK = Mathf.Min(1f, loadK + dt / RamUp);
                    var from = round.parent.TransformPoint(roundRest);
                    var fromRot = round.parent.rotation * roundRestRot;
                    var behind = barrel.TransformPoint(new Vector3(0f, 0f, -0.06f - RoundLen / 2f - 0.02f));
                    if (loadK < 0.6f)
                    {
                        float k = Mathf.SmoothStep(0f, 1f, loadK / 0.6f);
                        round.SetPositionAndRotation(Vector3.Lerp(from, behind, k), Quaternion.Slerp(fromRot, barrel.rotation, k));
                    }
                    else round.SetPositionAndRotation(Vector3.Lerp(behind, Chamber(), (loadK - 0.6f) / 0.4f), barrel.rotation);
                    if (loadK >= 1f) load = Load.Chambered;
                    break;
                case Load.Chambered:
                    round.SetPositionAndRotation(Chamber(), barrel.rotation);
                    break;
                case Load.Spent:
                    round.SetPositionAndRotation(Chamber(), barrel.rotation);
                    if (Time.time - shotAt >= SpentFor) { load = Load.Rising; loadK = 0f; }
                    break;
                case Load.Rising:
                    loadK = Mathf.Min(1f, loadK + dt / RiseTime);
                    round.localRotation = roundRestRot;
                    round.localPosition = roundRest + Vector3.down * (0.07f * (1f - Mathf.SmoothStep(0f, 1f, loadK)));
                    if (loadK >= 1f) load = Load.Cradle;
                    break;
            }
        }

        /// <summary>Where a chambered round sits: wholly inside the tube, just in from its open breech.</summary>
        Vector3 Chamber() => barrel.TransformPoint(new Vector3(0f, 0f, -0.06f + RoundLen / 2f + 0.005f));

        /// <summary>A shot is leaving now: lay the gun for it (`dist` tiles at `speed`), so the flash and the shell leave
        /// the laid muzzle.</summary>
        public void LayNow(float dist, float speed)
        {
            lay = LayFor(dist, speed, TargetY - MuzzleY);
            elev = lay;
            Head();
        }

        /// <summary>The frame a shot leaves: the gun up (to its lay) and, if it's stopped and hadn't dug in yet, the spades
        /// planted, their dust with the flash.</summary>
        public void Ready(WorldView.EV v)
        {
            if (elev < lay) { elev = lay; Head(); }
            if (Deploy < 0.6f && !v.E.Moving)
            {
                Deploy = 1f;
                Spades(1f);
                Bite(v.Rig.Root);
            }
        }

        void Head() => cradle.localRotation = cradleRest * Quaternion.Euler(-elev, 0f, 0f);

        void Spades(float planted)
        {
            var q = Quaternion.Euler(-SpadeSwing * planted, 0f, 0f);
            if (spadeL) spadeL.localRotation = spadeRestL * q;
            if (spadeR) spadeR.localRotation = spadeRestR * q;
        }

        /// <summary>The spades bite: dust kicked back off each blade.</summary>
        void Bite(Transform root)
        {
            if (spadeL) Fx.SpadeDust(spadeL.TransformPoint(tipL), -root.forward);
            if (spadeR) Fx.SpadeDust(spadeR.TransformPoint(tipR), -root.forward);
        }

        /// <summary>A spade's toothed edge in its own space: the far (-Z) end of its meshes, across their middle.</summary>
        static Vector3 Tip(Transform spade)
        {
            var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            var hi = -lo;
            foreach (var mf in spade.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null) continue;
                var b = mf.sharedMesh.bounds;
                for (int c = 0; c < 8; c++)
                {
                    var p = spade.InverseTransformPoint(mf.transform.TransformPoint(new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z)));
                    lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                }
            }
            return new Vector3((lo.x + hi.x) * 0.5f, (lo.y + hi.y) * 0.5f, lo.z);
        }
    }
}
