using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The flak track's act (MOTION.md flak_track): the SAM site's launcher shrunk onto a half-track as a quad flak mount.
    /// VehicleWorks ticks it; it owns the elevation axle's rotation (the barrel node: PezMotion never moves it, as the pods
    /// recoil instead), the search radar (flak_radar) and the rig's muzzle. PezMotion yaws the turret to the sim's aim and
    /// recoils the gun pods (barrel_l, barrel_r) in turn, left first.
    ///  - The mount rests 20 deg nose-up. When there's an aircraft to shoot (the one it last fired at, its attack order's
    ///    air target, or an enemy aircraft within range in the 45 deg its turret faces) it snaps up to it, 10 to 80 deg,
    ///    fast (a 0.07 s critically damped follow, up to 400 deg/s), follows it as it flies, holds 3 s after it's gone and
    ///    settles back to rest.
    ///  - Each burst (one sim shot, every 0.8 s) leaves the pod that recoils: this points the rig's muzzle at that pod's
    ///    mouth before the shot; the mount bucks 2 deg nose-up and its two spent wrappers flick out of the pod's outer side.
    ///  - The search radar turns at 20 rpm; while it tracks it carries on round to face forward and locks there.
    /// A model without those nodes gets the parts it has.
    /// </summary>
    public class FlakAct
    {
        const float Rest = 20f, Min = 10f, Max = 80f, Hold = 3f;

        readonly Rig rig;
        readonly Transform barrel, radar, muzzleL, muzzleR, ejectL, ejectR;
        readonly Quaternion barrelRest, radarRest;
        float pitch = Rest, pitchVel, held = Rest, lastAim = -99f, radarYaw, radarSpeed, seenFire, kickT = -1f, shotAt = -99f;
        bool primed, preparedShot;
        int shotTarget;

        public FlakAct(Rig rig, Transform model, int id)
        {
            this.rig = rig;
            barrel = rig.Barrel;
            radar = PezMotion.FindDeep(model, "flak_radar");
            muzzleL = PezMotion.FindDeep(model, "muzzle_l");
            muzzleR = PezMotion.FindDeep(model, "muzzle_r");
            ejectL = PezMotion.FindDeep(model, "eject_l");
            ejectR = PezMotion.FindDeep(model, "eject_r");
            if (barrel) barrelRest = barrel.localRotation;
            if (radar) radarRest = radar.localRotation;
            radarYaw = id * 47f % 360f;
        }

        /// <summary>Lay the pods for this authoritative burst before its muzzle is read. Only an aircraft visible
        /// to the shooting team can set the pitch; Tick keeps the follow, hold, kick and spent-wrapper act.</summary>
        public void ReadyShot(WorldView view, Entity shooter, Entity target)
        {
            if (!barrel || shooter.Team < 0 || target == null || target.Dead || !target.IsAir || target.IsCarried || target.Team == shooter.Team ||
                !view.World.IsVisibleTo(shooter.Team, target) || !view.Views.TryGetValue(target.Id, out var tv)) return;
            var d = tv.Rig.Root.position - barrel.position;
            pitch = held = Mathf.Clamp(Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, Min, Max);
            pitchVel = 0f;
            barrel.localRotation = barrelRest * Quaternion.Euler(-(pitch - Rest), 0f, 0f);
            shotTarget = target.Id;
            shotAt = lastAim = Time.time;
            kickT = 0f;
            preparedShot = true;
        }

        public void Tick(WorldView view, WorldView.EV v, float dt)
        {
            var e = v.E;
            float now = Time.time;
            if (!primed) { primed = true; seenFire = v.LastFire; }
            // A burst: WorldView played this track's fire event (Kick stamps LastFire; PezMotion recoiled a pod).
            if (v.LastFire != seenFire || preparedShot)
            {
                seenFire = v.LastFire;
                if (!preparedShot)
                {
                    shotAt = now;
                    kickT = 0f;
                    var evs = view.World.Events;
                    for (int i = evs.Count - 1, n = 0; i >= 0 && n < 200; i--, n++)
                        if (evs[i].A == e.Id && evs[i].Type == "fire") { shotTarget = evs[i].B; break; }
                }
                preparedShot = false;
                // The pod that just fired (Fire took NextTwin's turn): its wrappers flick out of its outer side.
                bool left = rig.Motion == null || ((rig.Motion.NextTwin - 1) & 1) == 0;
                var port = left ? ejectL : ejectR;
                if (port && barrel)
                {
                    var side = port.position - barrel.position; side.y = 0f;
                    side = side.sqrMagnitude > 1e-6f ? side.normalized : Vector3.up;
                    Fx.SpentWrapper(port.position, side);
                    Fx.SpentWrapper(port.position + Vector3.up * 0.03f, side);
                }
            }

            // What to shoot at, and the angle up to it.
            if (barrel && FindTarget(view, e, now, out var at))
            {
                var d = at - barrel.position;
                held = Mathf.Clamp(Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, Min, Max);
                lastAim = now;
            }
            bool tracking = now - lastAim < Hold;
            float goal = tracking ? held : Rest;
            pitch = Mathf.SmoothDamp(pitch, goal, ref pitchVel, tracking ? 0.07f : 0.3f, 400f, dt);
            float kick = 0f;
            if (kickT >= 0f)
            {
                kickT += dt;
                kick = 2f * Mathf.Sin(kickT * 30f) * Mathf.Exp(-kickT * 10f);
                if (kickT > 0.5f) kickT = -1f;
            }
            if (barrel) barrel.localRotation = barrelRest * Quaternion.Euler(-(pitch - Rest + kick), 0f, 0f);

            // The next burst leaves the pod that recoils next (PezMotion: even NextTwin is the left pod).
            bool nextLeft = rig.Motion == null || (rig.Motion.NextTwin & 1) == 0;
            var mz = nextLeft ? muzzleL : muzzleR;
            if (mz && barrel) rig.MuzzleLocal = barrel.InverseTransformPoint(mz.position);

            // The radar searches, and locks forward (carrying on round, never backing up) while the mount tracks.
            if (radar)
            {
                if (tracking)
                {
                    float left = Mathf.Repeat(-radarYaw, 360f);
                    if (left > 359.9f) left = 0f;
                    radarSpeed = Mathf.Clamp(left * 6f, 20f, 360f);
                    radarYaw += Mathf.Min(left, radarSpeed * dt);
                    if (left <= 0f) radarSpeed = 0f;
                }
                else
                {
                    radarSpeed = Mathf.MoveTowards(radarSpeed, 120f, 180f * dt);
                    radarYaw += radarSpeed * dt;
                }
                radarYaw = Mathf.Repeat(radarYaw, 360f);
                radar.localRotation = radarRest * Quaternion.Euler(0f, radarYaw, 0f);
            }
        }

        /// <summary>The aircraft it's fighting: the one it last fired at (for 3 s), its attack order's air target, or the
        /// nearest enemy aircraft within range in the 45 deg its turret faces (it raises the guns as one comes in).</summary>
        bool FindTarget(WorldView view, Entity e, float now, out Vector3 at)
        {
            at = default;
            if (e.Team < 0) return false; // a neutral exhibit has no team visibility state
            if (now - shotAt < Hold && Live(view, e.Team, shotTarget, out at)) return true;
            if (e.Order == Order.Attack && e.TargetId != 0 && Live(view, e.Team, e.TargetId, out at)) return true;
            Entity best = null;
            float bestScore = float.MaxValue, range = e.Def.Weapon != null ? e.Def.Weapon.Range + 0.5f : 8f;
            float facing = e.TurretFacing * Mathf.Rad2Deg;
            foreach (var o in view.World.Entities)
            {
                if (o.Dead || !o.IsAir || o.Team == e.Team || o.Team < 0 || o.IsCarried || !view.World.IsVisibleTo(e.Team, o)) continue;
                var d = o.Center - e.Pos;
                float dist = d.Length;
                if (dist > range) continue;
                float ang = Mathf.Abs(Mathf.DeltaAngle(facing, d.Angle * Mathf.Rad2Deg));
                if (ang > 45f) continue;
                float score = dist + ang * 0.05f;
                if (score < bestScore) { bestScore = score; best = o; }
            }
            return best != null && Live(view, e.Team, best.Id, out at);
        }

        static bool Live(WorldView view, int team, int id, out Vector3 at)
        {
            at = default;
            if (team < 0 || id == 0 || !view.Views.TryGetValue(id, out var tv) || tv.E.Dead || !tv.E.IsAir || !view.World.IsVisibleTo(team, tv.E)) return false;
            at = tv.Rig.Root.position;
            return true;
        }
    }
}
