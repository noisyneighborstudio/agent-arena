using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The laser tower's own motion (MOTION.md laser_tower); its yaw is PezMotion's (the turret follows the sim's aim).
    ///  - The emitter head (the barrel node, on trunnions in the yoke) pitches toward what it's shooting at, -30 to +40 deg
    ///    at up to 120 deg/s, following the target as it moves: down at tanks, up at aircraft. After a fight it holds 3 s,
    ///    then levels off.
    ///  - The shot charges: in the last 0.4 s before the next shot is due (the sim's cooldown after the last one, while it's
    ///    still engaged) the four pylon coils (coil_0 .. coil_3) light one after another from the bottom, and the lens
    ///    brightens. On the shot the lens flashes, the coils drain from the top down in 0.25 s, and the head's rear vent
    ///    breathes out a wisp of heat.
    ///  - Idle, the coils breathe on a 2.5 s sine and the lens glows softly.
    ///  - The coils cast light (a <see cref="GlowCaster"/> in the middle of the pylon): cyan on the cream pylon, the pad
    ///    and the ground around it, following the coils' glow (idle breath, the charge climbing, a flash on the shot, the
    ///    drain), halved on low power, out when offline, and seen only by those who see the tower's live state.
    /// Every glow is scaled by the building's power glow (half on low power, nearly dark offline), and a render whose team
    /// only remembers the tower under fog keeps the neutral glow WorldView gives it. A model without those nodes gets the
    /// parts it has.
    /// </summary>
    public class LaserWorks
    {
        readonly Transform barrel, vent;
        readonly Quaternion barrelRest;
        readonly Renderer[] coils;
        readonly Renderer lens;
        readonly GlowCaster light;
        readonly float phase;
        float pitch, seenFire = -99f, shotAt = -99f, puffAt = -1f;
        int target;

        const float Charge = 0.4f, Drain = 0.25f, Hold = 3f;

        LaserWorks(Transform model, int seed)
        {
            barrel = PezMotion.FindDeep(model, "barrel");
            vent = PezMotion.FindDeep(model, "vent");
            if (barrel) barrelRest = barrel.localRotation;
            var list = new System.Collections.Generic.List<Renderer>();
            for (Transform c; (c = PezMotion.FindDeep(model, "coil_" + list.Count)) != null; )
                list.Add(c.GetComponentInChildren<Renderer>());
            coils = list.ToArray();
            var l = PezMotion.FindDeep(model, "barrel__M_E_Cyan");
            lens = l ? l.GetComponent<Renderer>() : null;
            phase = seed * 0.53f;
            // Halfway up the coils (coil_0 .. coil_3 wind round the stem from 0.45 to 1.25 up, 0.18 out), on the near side
            // of the ring: it lights the pad below, the collar above and the ground around.
            light = GlowCaster.For(model.gameObject, new Vector3(0f, 0.8f, 0f), PezPalette.EmissiveCyanLaserOptics,
                light: 2.8f, range: 2.4f, pool: 1.2f, deck: 0.37f, deckRadius: 0.5f, ring: 0.24f);
        }

        public static LaserWorks For(GameObject model, int seed) => new LaserWorks(model.transform, seed);

        /// <summary>Call each frame for a complete tower, after WorldView.Power (it sets the glow this scales), with
        /// Fx.Audience set. `live`: the view's team sees the tower's state.</summary>
        public void Tick(WorldView wv, WorldView.EV v, bool live, float dt)
        {
            var e = v.E;
            float now = Time.time;
            // A new shot (WorldView.Kick stamps LastFire when it plays the shot): find whom it was at.
            if (v.LastFire != seenFire)
            {
                seenFire = v.LastFire;
                shotAt = now;
                puffAt = now + 0.12f;
                var evs = wv.World.Events;
                for (int i = evs.Count - 1, n = 0; i >= 0 && n < 200; i--, n++)
                    if (evs[i].A == e.Id && evs[i].Type == "shot") { target = evs[i].B; break; }
            }
            bool engaged = now - shotAt < Hold;
            float cooldown = e.Def.Weapon != null ? e.Def.Weapon.Cooldown : 1f;

            // Pitch: toward the target's live position (the beam lands 0.3 above a ground target, on an aircraft itself).
            if (barrel)
            {
                float goal = 0f;
                if (engaged && wv.Views.TryGetValue(target, out var tv) && !tv.E.Dead)
                {
                    var at = tv.Rig.Root.position + (tv.Rig.Altitude > 0f ? Vector3.zero : Vector3.up * 0.3f);
                    var d = at - barrel.position;
                    goal = Mathf.Clamp(Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, -30f, 40f);
                }
                else if (engaged) goal = pitch; // its target is gone: hold where it was looking
                pitch = Mathf.MoveTowards(pitch, goal, (engaged ? 120f : 30f) * dt);
                barrel.localRotation = barrelRest * Quaternion.Euler(-pitch, 0f, 0f); // +x tips the nose down
            }

            if (puffAt > 0f && now >= puffAt)
            {
                puffAt = -1f;
                if (vent && live) Fx.Steam(vent.position, 0.13f);
            }

            // The power glow: 1 powered, 0.5 on low power, easing down to 0.1 offline (WorldView.Power).
            float power = v.GlowK < 0f ? 1f : v.GlowK;
            // The charge for the next shot: 0..1 over the last 0.4 s before it's due; a charge that isn't fired (the
            // target died or left) bleeds away over 0.4 s.
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
                // Charging: each coil lights a quarter of the way after the one below it.
                float up = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(charge * coils.Length - i));
                // Fired: the coils were full and drain from the top down.
                float k = Mathf.Clamp01((since - (coils.Length - 1 - i) * 0.05f) / Drain);
                float down = since >= 0f && since < Drain + 0.2f ? 1f - k : 0f;
                float g = Mathf.Lerp(breath, 1.3f, Mathf.Max(up, down));
                sum += g;
                if (live) PezShade.Set(coils[i], Dim(g * power), g * power); // a remembered tower keeps WorldView's neutral glow
            }
            if (lens && live)
            {
                float g = (Mathf.Lerp(0.5f + 0.1f * Mathf.Sin((now / 2.5f + phase) * 6.2831853f), 1.6f, charge) + 2.4f * flash) * power;
                PezShade.Set(lens, Dim(g), g);
            }
            // The coils' light: a steady 0.53 to 0.59 on the idle breath, rising gently with the charge as the coils fill (to
            // about 0.6 when all four are lit, which is the moment of the shot) and a little more in the flash (to about 0.66,
            // the brightest moment), then down with the drain: a tower firing shows it on its pad without flooding it. Its
            // power is the coils' (half on low power), but it goes right out as an unpaid tower dims to its 0.1 rather than
            // stopping there. Fx.Audience keeps it from the views that only remember the tower under fog.
            float fill = coils.Length > 0 ? Mathf.Clamp01((sum / coils.Length - breath) / (1.3f - breath)) : 0f;
            float lit = power >= 0.5f ? power : Mathf.InverseLerp(0.1f, 0.5f, power) * 0.5f;
            light.Set((0.45f + 0.3f * breath + 0.05f * fill + 0.06f * flash) * lit, Fx.Audience);
        }

        // The lamp body darkens with its light (as WorldView.Power), but an idle coil still reads as cyan wire; an offline
        // tower's is dark.
        static float Dim(float glow) => Mathf.Lerp(0.15f, 1f, Mathf.InverseLerp(0.1f, 1f, glow));
    }
}
