using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The SAM site at work (MOTION.md sam_site). PezMotion yaws the turret to the sim's aim; this drives the rest:
    ///  - The rack (the model's barrel node, the elevation axle with a two-tube pod on each end) rests 25 deg nose-up. While
    ///    the sim has it engaging (FireAt ran in the last 0.3 s of sim time), it raises to the air target it is facing,
    ///    10 to 70 deg (eased, up to 140 deg/s), holds that for 3 s after the last shot, then settles back.
    ///  - The search radar (radar) turns at 20 rpm while it searches; on contact it carries on round to face forward and
    ///    locks there, and spins up again after the hold.
    ///  - Each launch empties the next loaded tube (the pods take turns, top tubes first): its round
    ///    (missile_N, pivot at the mouth) drops back out of sight, smoke blows out of the pod's back and dust rings out on
    ///    the ground, and the rack bucks 3 deg (nose up from a top tube, down from a bottom one). 2.4 s later a new round
    ///    slides up into the mouth over 0.45 s. The shot and its flash leave from the tube that fires: this sets the rig's
    ///    muzzle to the next tube's mouth.
    ///  - Offline (upkeep unpaid) the rack lowers to 4 deg and the radar stops; low power halves the radar.
    /// A model without those nodes gets the parts it has.
    /// </summary>
    public class SamWorks
    {
        const float Rest = 25f, Hidden = -0.16f, Reload = 2.4f, Slide = 0.45f;
        static readonly int[] Order = { 0, 2, 1, 3 };

        readonly Rig rig;
        readonly Transform barrel, radar;
        readonly Quaternion barrelRest, radarRest;
        readonly Transform[] rounds;
        readonly Vector3[] roundRest;
        readonly float[] off, reloadAt;
        float pitch = Rest, pitchVel, held = Rest, lastAim = -99f, radarYaw, radarSpeed, seenFire, kickT = -1f, kickSign = 1f;
        int next;

        SamWorks(Rig rig, float lastFire, int seed)
        {
            this.rig = rig;
            var model = rig.Model.transform;
            barrel = rig.Barrel;
            radar = PezMotion.FindDeep(model, "radar");
            if (barrel) barrelRest = barrel.localRotation;
            if (radar) radarRest = radar.localRotation;
            var list = new System.Collections.Generic.List<Transform>();
            for (Transform c; (c = PezMotion.FindDeep(model, "missile_" + list.Count)) != null; ) list.Add(c);
            rounds = list.ToArray();
            roundRest = new Vector3[rounds.Length];
            off = new float[rounds.Length];
            reloadAt = new float[rounds.Length];
            for (int i = 0; i < rounds.Length; i++) roundRest[i] = rounds[i].localPosition;
            radarYaw = seed * 47f % 360f;
            seenFire = lastFire;
            AimMuzzle();
        }

        public static SamWorks For(WorldView.EV v) => new SamWorks(v.Rig, v.LastFire, v.E.Id);

        int Tube(int i) => Order.Length == rounds.Length ? Order[i % Order.Length] : i % Mathf.Max(1, rounds.Length);

        /// <summary>Shots leave from the mouth of the tube that fires next (missile nodes sit under the barrel).</summary>
        void AimMuzzle()
        {
            if (rounds.Length == 0 || barrel == null) return;
            var r = rounds[Tube(next)];
            rig.MuzzleLocal = barrel.InverseTransformPoint(r.parent.TransformPoint(roundRest[Tube(next)] + Vector3.forward * 0.1f));
        }

        public void Tick(WorldView view, WorldView.EV v, float dt)
        {
            var e = v.E;
            var w = view.World;
            float now = Time.time;
            bool offline = e.Offline, low = w.Teams[e.Team].LowPower;

            // A launch: the view played this site's fire event (WorldView.Kick stamps LastFire).
            if (v.LastFire > seenFire)
            {
                seenFire = v.LastFire;
                int n = rounds.Length;
                if (n > 0)
                {
                    Launch(Tube(next), now);
                    int pick = (next + 1) % n; // all empty: the sim fires anyway, from the next tube in turn
                    for (int i = 1; i <= n; i++) if (off[Tube(next + i)] >= 0f) { pick = (next + i) % n; break; }
                    next = pick;
                    AimMuzzle();
                }
            }

            // What it's shooting at: the sim fires at an air unit it faces; find it and raise the rack to it.
            bool engaged = !offline && w.Time - e.LastFiredAt < 0.3f;
            if (engaged && barrel && FindTarget(view, e, out var at))
            {
                var d = at - barrel.position;
                held = Mathf.Clamp(Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, 10f, 70f);
                lastAim = now;
            }
            bool tracking = !offline && now - lastAim < 3f;
            float goal = offline ? 4f : tracking ? held : Rest;
            pitch = Mathf.SmoothDamp(pitch, goal, ref pitchVel, 0.15f, 140f, dt);
            float kick = 0f;
            if (kickT >= 0f)
            {
                kickT += dt;
                kick = kickSign * 3.2f * Mathf.Sin(kickT * 26f) * Mathf.Exp(-kickT * 9f);
                if (kickT > 0.6f) kickT = -1f;
            }
            if (barrel) barrel.localRotation = barrelRest * Quaternion.Euler(-(pitch - Rest + kick), 0f, 0f);

            // The radar searches, and locks forward (carrying on round, never backing up) while it tracks.
            if (radar)
            {
                if (tracking)
                {
                    float left = Mathf.Repeat(-radarYaw, 360f);
                    if (left > 359.9f) left = 0f;
                    radarSpeed = Mathf.Clamp(left * 6f, 20f, 240f); // closes in and settles on the stop
                    radarYaw += Mathf.Min(left, radarSpeed * dt);
                    if (left <= 0f) radarSpeed = 0f;
                }
                else
                {
                    float want = offline ? 0f : low ? 60f : 120f;
                    radarSpeed = Mathf.MoveTowards(radarSpeed, want, 120f * dt);
                    radarYaw += radarSpeed * dt;
                }
                radarYaw = Mathf.Repeat(radarYaw, 360f);
                radar.localRotation = radarRest * Quaternion.Euler(0f, radarYaw, 0f);
            }

            // Reloads: a new round slides up into an empty tube.
            for (int i = 0; i < rounds.Length; i++)
            {
                if (off[i] >= 0f) continue;
                float k = Mathf.Clamp01((now - reloadAt[i]) / Slide);
                float eased = 1f - (1f - k) * (1f - k) * (1f - k);
                off[i] = k >= 1f ? 0f : Mathf.Min(-0.0001f, Hidden * (1f - eased));
                rounds[i].localPosition = roundRest[i] + Vector3.forward * off[i];
            }
        }

        void Launch(int k, float now)
        {
            off[k] = Hidden;
            reloadAt[k] = now + Reload;
            rounds[k].localPosition = roundRest[k] + Vector3.forward * Hidden;
            kickT = 0f;
            kickSign = roundRest[k].y > 0f ? 1f : -1f; // the thrust above the axle tips the nose up, below it down
            if (!barrel) return;
            // Backblast: smoke out of the back of the pod, and a dust ring where it hits the ground.
            var rear = barrel.TransformPoint(roundRest[k] + Vector3.back * 0.52f);
            Fx.BarrelSmoke(rear);
            Fx.GroundRing(rig.Root.position, 0.9f, 0.35f, 0);
        }

        /// <summary>The air unit the sim is shooting at: the nearest enemy aircraft in range near the turret's facing.</summary>
        static bool FindTarget(WorldView view, Entity e, out Vector3 at)
        {
            at = default;
            Entity best = null;
            float bestScore = float.MaxValue, range = e.Def.Weapon != null ? e.Def.Weapon.Range + 1.5f : 9f;
            float facing = e.TurretFacing * Mathf.Rad2Deg;
            foreach (var o in view.World.Entities)
            {
                if (o.Dead || !o.IsAir || o.Team == e.Team || o.Team < 0 || o.IsCarried) continue;
                var d = o.Center - e.Pos;
                float dist = d.Length;
                if (dist > range) continue;
                float ang = Mathf.Abs(Mathf.DeltaAngle(facing, d.Angle * Mathf.Rad2Deg));
                if (ang > 45f) continue;
                float score = dist + ang * 0.05f;
                if (score < bestScore) { bestScore = score; best = o; }
            }
            if (best == null) return false;
            at = view.Views.TryGetValue(best.Id, out var tv) && tv.Rig.Root.gameObject.activeInHierarchy
                ? tv.Rig.Root.position + Vector3.up * 0.1f
                : WorldView.W(best.Center, 2.4f);
            return true;
        }
    }
}
