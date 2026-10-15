using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>The two fixed-wing candy aircraft's own hardware. WorldView keeps root, altitude and body banking;
    /// this class alone owns sb_bay/elevon/payload, rp_prop/sensor/round nodes. No generic turret or spinner.</summary>
    public sealed class StrikeAircraftWorks
    {
        readonly Rig rig;
        readonly bool bomber;
        readonly Transform bayL, bayR, elevonL, elevonR, payload, prop, sensorYaw, sensorPitch;
        readonly Transform[] rounds = new Transform[2], muzzles = new Transform[2];
        readonly Vector3 payloadRest;
        readonly Quaternion bayLRest, bayRRest, elevonLRest, elevonRRest, propRest, sensorYawRest, sensorPitchRest;
        readonly float phase;
        float firedAt = -99f, propAngle, propSpeed, bayAngle;
        int nextRail, firedRail, shotTarget;
        int fireFrame = -1;
        Vector3 firedMuzzle;

        StrikeAircraftWorks(Rig rig, string key, int id)
        {
            this.rig = rig;
            bomber = key == "stealth_bomber";
            phase = id * .73f;
            var model = rig.Model.transform;
            bayL = PezMotion.FindDeep(model, "sb_bay_l"); bayR = PezMotion.FindDeep(model, "sb_bay_r");
            elevonL = PezMotion.FindDeep(model, "sb_elevon_l"); elevonR = PezMotion.FindDeep(model, "sb_elevon_r");
            payload = PezMotion.FindDeep(model, "sb_payload"); prop = PezMotion.FindDeep(model, "rp_prop");
            sensorYaw = PezMotion.FindDeep(model, "rp_sensor_yaw"); sensorPitch = PezMotion.FindDeep(model, "rp_sensor_pitch");
            rounds[0] = PezMotion.FindDeep(model, "rp_round_l"); rounds[1] = PezMotion.FindDeep(model, "rp_round_r");
            muzzles[0] = PezMotion.FindDeep(model, "rp_muzzle_l"); muzzles[1] = PezMotion.FindDeep(model, "rp_muzzle_r");
            if (bayL) bayLRest = bayL.localRotation; if (bayR) bayRRest = bayR.localRotation;
            if (elevonL) elevonLRest = elevonL.localRotation; if (elevonR) elevonRRest = elevonR.localRotation;
            if (payload) payloadRest = payload.localPosition; if (prop) propRest = prop.localRotation;
            if (sensorYaw) sensorYawRest = sensorYaw.localRotation; if (sensorPitch) sensorPitchRest = sensorPitch.localRotation;
        }

        public static StrikeAircraftWorks For(Rig rig, string key, int id) => new StrikeAircraftWorks(rig, key, id);

        /// <summary>Called once per authoritative fire, before the muzzle is read. One event releases ONE candy;
        /// rail alternation is independent of PezMotion's generic barrel recoil/twin counter.</summary>
        public void ReadyShot(World world, Entity shooter, Entity target)
        {
            firedAt = Time.time;
            fireFrame = Time.frameCount;
            shotTarget = target != null && shooter.Team >= 0 && world.IsVisibleTo(shooter.Team, target) ? target.Id : 0;
            firedRail = nextRail;
            nextRail = 1 - nextRail;
            firedMuzzle = bomber ? (rig.Barrel ? rig.Barrel.TransformPoint(rig.MuzzleLocal) : rig.Root.position)
                                : muzzles[firedRail] ? muzzles[firedRail].position : rig.Root.position;
            // The bomber's permanent centre throat is wider than its candy, even with shutters closed.
            // Surprise first-frame shots therefore need no door snap or invented gameplay delay.
        }

        public Vector3 Muzzle(bool fired) => fired && fireFrame == Time.frameCount ? firedMuzzle
            : bomber ? (rig.Barrel ? rig.Barrel.TransformPoint(rig.MuzzleLocal) : rig.Root.position)
            : muzzles[nextRail] ? muzzles[nextRail].position : rig.Root.position;

        public void Tick(WorldView view, Entity e, bool seesState, float dt)
        {
            float t = Time.time - firedAt;
            // A view with no right to private current state holds the closed loaded rest pose.
            bool live = seesState && !e.Dead;
            if (bomber)
            {
                bool aiming = live && !e.Landed && HasVisibleTarget(view.World, e, out _);
                float goal = (aiming && e.Cooldown <= .45f) || (live && t < .42f) ? 70f : 0f;
                bayAngle = Mathf.MoveTowards(bayAngle, goal, dt * (goal > bayAngle ? 240f : 150f));
                if (bayL) bayL.localRotation = bayLRest * Quaternion.Euler(0, 0, bayAngle);
                if (bayR) bayR.localRotation = bayRRest * Quaternion.Euler(0, 0, -bayAngle);
                // The replacement candy comes from INSIDE its solid ceiling, never scales in the open.
                if (payload)
                {
                    float refill = live ? Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.2f, 1.05f, t)) : 1f;
                    payload.localPosition = payloadRest + Vector3.up * (.15f * (1f - refill));
                }
                // Real turn/bank drives the trailing elevons; no second body owner.
                float bank = Mathf.DeltaAngle(0f, rig.Body.localEulerAngles.z);
                float flap = live && !e.Landed ? Mathf.Clamp(bank * .32f, -8f, 8f) : 0f;
                if (elevonL) elevonL.localRotation = elevonLRest * Quaternion.Euler(flap, 0, 0);
                if (elevonR) elevonR.localRotation = elevonRRest * Quaternion.Euler(-flap, 0, 0);
            }
            else
            {
                // Stream-friendly 210 rpm, ramped down on its real landed/refuelling state.
                propSpeed = Mathf.MoveTowards(propSpeed, live && !e.Landed ? 1260f : 0f, 1800f * dt);
                propAngle = Mathf.Repeat(propAngle + propSpeed * dt, 360f);
                if (prop) prop.localRotation = propRest * Quaternion.Euler(0, 0, propAngle);
                float yaw = 0f, pitch = 0f;
                if (live && !e.Landed)
                {
                    if (sensorYaw && HasVisibleTarget(view.World, e, out var target) && view.Views.TryGetValue(target.Id, out var tv))
                    {
                        var d = rig.Root.InverseTransformDirection(tv.Rig.Root.position + Vector3.up * .3f - sensorYaw.position);
                        yaw = Mathf.Clamp(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, -60f, 60f);
                        pitch = Mathf.Clamp(-Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, -10f, 55f);
                    }
                    else { yaw = Mathf.Sin(Time.time * .35f + phase) * 28f; pitch = 20f; }
                }
                if (sensorYaw) sensorYaw.localRotation = Quaternion.Slerp(sensorYaw.localRotation, sensorYawRest * Quaternion.Euler(0, yaw, 0), 1f - Mathf.Exp(-dt * 5f));
                if (sensorPitch) sensorPitch.localRotation = Quaternion.Slerp(sensorPitch.localRotation, sensorPitchRest * Quaternion.Euler(pitch, 0, 0), 1f - Mathf.Exp(-dt * 5f));
                for (int i = 0; i < rounds.Length; i++) if (rounds[i])
                {
                    // One loaded missile per rail is a ready-state illustration, not invented finite ammo stock.
                    // The spent rail reloads behind the pylon in the actual five-second cooldown.
                    var p = rounds[i].localPosition;
                    p.y = .12f + (live && i == firedRail ? .11f * (1f - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.15f, .9f, t))) : 0f);
                    rounds[i].localPosition = p;
                }
            }
        }

        bool HasVisibleTarget(World world, Entity e, out Entity target)
        {
            target = world.Get(e.TargetId);
            if ((target == null || target.Team == e.Team) && Time.time - firedAt < 3f) target = world.Get(shotTarget);
            return e.Team >= 0 && target != null && !target.Dead && !target.IsCarried && target.Team != e.Team &&
                e.Def.Weapon != null && e.Def.Weapon.CanHit(target.Def) && world.IsVisibleTo(e.Team, target) &&
                target.DistFrom(e.Pos) <= e.Def.Weapon.Range + .5f;
        }

        /// <summary>The real projectile carries a copy of the installed loaded candy. The projectile remains sim-owned.</summary>
        public void ArmProjectile(Transform shot)
        {
            var round = bomber ? payload : rounds[fireFrame == Time.frameCount ? firedRail : nextRail];
            if (!round) return;
            float host = Mathf.Max(1e-4f, shot.localScale.y * .5f);
            shot.localScale = Vector3.one * host;
            if (bomber)
            {
                var glow = shot.GetComponent<Renderer>(); if (glow) glow.enabled = false;
                var trail = shot.GetComponent<TrailRenderer>(); if (trail) trail.enabled = false;
            }
            var copy = Object.Instantiate(round.gameObject, shot, false).transform;
            var ls = round.lossyScale;
            copy.localScale = new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)) / host;
            copy.localPosition = Vector3.forward * (.04f / host);
            copy.localRotation = Quaternion.identity;
            foreach (var c in copy.GetComponentsInChildren<Transform>(true)) c.gameObject.layer = shot.gameObject.layer;
        }

        /// <summary>A shared detached wreck must contain only the same fixed loaded illustration for every viewer.</summary>
        public static void Rest(GameObject model)
        {
            var root = model.transform;
            foreach (string name in new[] { "sb_bay_l", "sb_bay_r", "sb_elevon_l", "sb_elevon_r", "rp_sensor_yaw", "rp_sensor_pitch" })
            {
                var node = PezMotion.FindDeep(root, name);
                if (node) node.localRotation = Quaternion.identity;
            }
            var payload = PezMotion.FindDeep(root, "sb_payload");
            if (payload) payload.localPosition = new Vector3(0, .19f, .015f);
            foreach (string name in new[] { "rp_round_l", "rp_round_r" })
            {
                var node = PezMotion.FindDeep(root, name);
                if (node) { var p = node.localPosition; p.y = .12f; p.z = .10f; node.localPosition = p; }
            }
        }
    }
}
