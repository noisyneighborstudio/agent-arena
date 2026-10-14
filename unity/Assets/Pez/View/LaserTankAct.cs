using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The laser tank's act (MOTION.md laser_tank): a laser tower on tracks. VehicleWorks ticks it; it owns only the
    /// projector's pitch (the barrel node's rotation: PezMotion keeps its position, and the laser tank has no recoil), the
    /// three pylon coils (coil_0 .. coil_2), the three coil sleeves on the barrel (barrel_coil_0 .. 2), the lens and the light they cast. PezMotion yaws the turret to the sim's aim.
    ///  - The projector pitches toward what it's shooting at (the target of its last beam, or of its attack order), -3 to
    ///    +45 deg at up to 200 deg/s: level at tanks, up at aircraft (the beam cannon hits both). After a fight it holds 3 s,
    ///    then levels off at 30 deg/s.
    ///  - The beam charges: in the last 0.5 s before the next shot is due (the sim's 2 s cooldown after the last one, while
    ///    it's engaged) the six coils light one after another, up the pylon and out along the barrel, to 1.3, and the lens brightens. On the shot the
    ///    lens flashes, the coils drain back from the lens (0.04 s apart, 0.25 s each) and the head's rear vent breathes out a wisp of heat.
    ///    A charge that isn't fired bleeds away over 0.4 s. Idle, the coils breathe on a 2.5 s sine and the lens glows softly.
    ///  - The coils cast light (a <see cref="GlowCaster"/> halfway up the pylon, toward the camera): cyan on the head, the
    ///    deck and the ground, following the coils' glow, swelling with the charge and flashing with the shot.
    /// Effects go to Fx.Audience (VehicleWorks sets it to who can see the tank). A model without those nodes gets the parts
    /// it has.
    /// </summary>
    public class LaserTankAct
    {
        const float Charge = 0.5f, Drain = 0.25f, Hold = 3f, MinPitch = -3f, MaxPitch = 45f;

        readonly Transform barrel, vent;
        readonly Quaternion barrelRest;
        readonly Renderer[] coils;
        readonly Renderer lens;
        readonly GlowCaster light;
        readonly float phase;
        float pitch, seenFire, shotAt = -99f, puffAt = -1f;
        bool primed, preparedShot;
        int target;

        public LaserTankAct(Rig rig, Transform model, int id)
        {
            barrel = rig.Barrel;
            vent = PezMotion.FindDeep(model, "vent");
            if (barrel) barrelRest = barrel.localRotation;
            var list = new System.Collections.Generic.List<Renderer>();
            Vector3 mid = Vector3.zero;
            for (Transform c; (c = PezMotion.FindDeep(model, "coil_" + list.Count)) != null; )
            {
                list.Add(c.GetComponentInChildren<Renderer>());
                mid += model.InverseTransformPoint(c.position);
            }
            if (list.Count > 0) mid /= list.Count;
            // ...then the sleeves out along the barrel (barrel_coil_0 at the breech .. the lens): the charge climbs the pylon
            // and runs out to the lens.
            for (int k = 0; PezMotion.FindDeep(model, "barrel_coil_" + k) is Transform b; k++) list.Add(b.GetComponentInChildren<Renderer>());
            coils = list.ToArray();
            var l = PezMotion.FindDeep(model, "barrel__M_E_Cyan");
            lens = l ? l.GetComponent<Renderer>() : null;
            phase = id * 0.53f;
            // In the middle of the coils, a little toward the camera: it lights the head above, the deck round the pylon
            // and the ground beside the tracks. A third of the tower's strength: it tints, it doesn't flood.
            light = GlowCaster.For(model.gameObject, mid, PezPalette.EmissiveCyanLaserOptics, light: 1.5f, range: 1.6f, pool: 0.75f, ring: 0.14f);
        }

        /// <summary>Lay the projector for this authoritative beam before its muzzle is read; a hidden target cannot
        /// reveal itself through the barrel's pitch. Tick performs the existing charge, heat and hold afterwards.</summary>
        public void ReadyShot(WorldView view, Entity shooter, Entity target)
        {
            if (!barrel || shooter.Team < 0 || target == null || target.Dead || target.IsCarried || target.Team == shooter.Team ||
                !view.World.IsVisibleTo(shooter.Team, target) || !view.Views.TryGetValue(target.Id, out var tv)) return;
            var at = tv.Rig.Root.position + (tv.Rig.Altitude > 0f ? Vector3.zero : Vector3.up * 0.3f);
            var d = at - barrel.position;
            pitch = Mathf.Clamp(Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, MinPitch, MaxPitch);
            barrel.localRotation = barrelRest * Quaternion.Euler(-pitch, 0f, 0f);
            this.target = target.Id;
            shotAt = Time.time;
            puffAt = shotAt + 0.12f;
            preparedShot = true;
        }

        public void Tick(WorldView view, WorldView.EV v, float dt)
        {
            var e = v.E;
            float now = Time.time;
            // A new beam (WorldView.Kick stamps LastFire when it plays the shot): find whom it was at.
            if (!primed) { primed = true; seenFire = v.LastFire; }
            if (v.LastFire != seenFire || preparedShot)
            {
                seenFire = v.LastFire;
                if (!preparedShot)
                {
                    shotAt = now;
                    puffAt = now + 0.12f;
                    var evs = view.World.Events;
                    for (int i = evs.Count - 1, n = 0; i >= 0 && n < 200; i--, n++)
                        if (evs[i].A == e.Id && evs[i].Type == "shot") { target = evs[i].B; break; }
                }
                preparedShot = false;
            }
            bool engaged = now - shotAt < Hold;
            float cooldown = e.Def.Weapon != null ? e.Def.Weapon.Cooldown : 2f;

            // Pitch: toward the target's live position (a ground target 0.3 up, an aircraft itself). Before the first beam
            // it already looks at its attack order's target, so the shot leaves along the projector.
            if (barrel)
            {
                int aim = engaged ? target : e.Order == Order.Attack ? e.TargetId : 0;
                float goal = 0f;
                bool found = false;
                if (e.Team >= 0 && aim != 0 && view.Views.TryGetValue(aim, out var tv) && !tv.E.Dead && view.World.IsVisibleTo(e.Team, tv.E))
                {
                    var at = tv.Rig.Root.position + (tv.Rig.Altitude > 0f ? Vector3.zero : Vector3.up * 0.3f);
                    var d = at - barrel.position;
                    goal = Mathf.Clamp(Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, MinPitch, MaxPitch);
                    found = true;
                }
                else if (engaged) goal = pitch; // its target is gone: hold where it was looking
                pitch = Mathf.MoveTowards(pitch, goal, (found || engaged ? 200f : 30f) * dt);
                barrel.localRotation = barrelRest * Quaternion.Euler(-pitch, 0f, 0f); // +x tips the nose down
            }

            if (puffAt > 0f && now >= puffAt)
            {
                puffAt = -1f;
                if (vent) Fx.Steam(vent.position, 0.1f);
            }

            // The charge for the next beam: 0..1 over the last 0.5 s before it's due; one that isn't fired bleeds away.
            float due = shotAt + cooldown, charge = 0f;
            if (engaged)
            {
                if (now < due) charge = Mathf.Clamp01((now - (due - Charge)) / Charge);
                else charge = 1f - Mathf.Clamp01((now - due - 0.15f) / 0.4f);
            }
            float since = now - shotAt;
            float breath = 0.35f + 0.1f * Mathf.Sin((now / 2.5f + phase) * 6.2831853f);
            float flash = since < 0.3f ? 1f - since / 0.3f : 0f, sum = 0f;
            for (int i = 0; i < coils.Length; i++)
            {
                float up = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(charge * coils.Length - i));          // up the pylon, out the barrel
                float k = Mathf.Clamp01((since - (coils.Length - 1 - i) * 0.04f) / Drain);              // back from the lens
                float down = since >= 0f && since < Drain + coils.Length * 0.04f ? 1f - k : 0f;
                float g = Mathf.Lerp(breath, 1.3f, Mathf.Max(up, down));
                sum += g;
                if (coils[i]) PezShade.Set(coils[i], Dim(g), g);
            }
            if (lens)
            {
                float g = Mathf.Lerp(0.5f + 0.1f * Mathf.Sin((now / 2.5f + phase) * 6.2831853f), 1.6f, charge) + 2.4f * flash;
                PezShade.Set(lens, Dim(g), g);
            }
            // The light: about 0.45 on the idle breath, rising to about 0.8 as the coils fill, 0.95 in the flash.
            float fill = coils.Length > 0 ? Mathf.Clamp01((sum / coils.Length - breath) / (1.3f - breath)) : 0f;
            light.Set(0.33f + 0.3f * breath + 0.35f * fill + 0.15f * flash, Fx.Audience);
        }

        // The coil body darkens with its light, but an idle coil still reads as cyan wire.
        static float Dim(float glow) => Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.1f, 1f, glow));
    }
}
