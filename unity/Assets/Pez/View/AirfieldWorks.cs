using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The airfield at work (MOTION.md airfield). The lift in the middle of the runway is where the sim puts new aircraft.
    ///  - Producing (an aircraft in this airfield's queue): the approach lights (approach_0..4: four chevrons pointing the
    ///    way out along -Z, then a threshold bar) glow a steady low amber, and the tower radar turns at 120 deg/s.
    ///  - Launch: 1.5 s before the aircraft is done the lift goes down its well (1.2 s, eased); when it's done the aircraft
    ///    stands on the deck and rides it up (0.7 s, eased), then lifts off. From 2 s before until 1.5 s after, the approach
    ///    lights chase toward -Z (a sweep every 0.5 s) and the tower beacon blinks at 2 Hz.
    ///  - Idle: the lights are dark, the radar turns at 40 deg/s and the beacon blinks once every 1.5 s.
    ///  - Low power (the sim builds at half speed): the lift and the radar run at half speed (PezMotion's machine rate and
    ///    here), the chase sweeps once a second, every glow is halved and the beacon blinks every 3 s.
    ///  - Always: the windsock (windsock) turns to point downwind (FxSystems.Wind) and droops as the wind drops.
    /// A render whose team only remembers the airfield under fog shows the lights dark and no launch.
    /// A model without those nodes gets the parts it has.
    /// </summary>
    public class AirfieldWorks
    {
        const float DeckTop = 0.15f;          // the lift deck's top above the lift node
        const float Lead = 1.5f;              // seconds before an aircraft is done that the lift starts down (1.2 s travel)
        readonly PezMotion motion;
        readonly Transform lift, radar, sock;
        readonly float liftRestY, sockYaw0, phase;
        readonly Quaternion radarRest, sockRest;
        readonly Renderer[] rows;
        readonly Renderer beacon;
        float radarYaw, radarSpeed = 40f, after = 99f, sockYaw, sockDroop = -1f;
        bool lowered;
        WorldView.EV rider;                   // the aircraft riding the lift up
        float riderT;

        AirfieldWorks(Transform model, PezMotion motion, int seed)
        {
            this.motion = motion;
            lift = PezMotion.FindDeep(model, "lift");
            radar = PezMotion.FindDeep(model, "radar");
            sock = PezMotion.FindDeep(model, "windsock");
            if (lift) liftRestY = lift.localPosition.y;
            if (radar) radarRest = radar.localRotation;
            if (sock)
            {
                // The sock's heading as authored, read from where its cloth is (the import mirrors x, so it's not assumed).
                var b = new Bounds(sock.position, Vector3.zero);
                foreach (var r in sock.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                var axis = b.center - sock.position;
                sockYaw0 = Mathf.Atan2(axis.x, axis.z) * Mathf.Rad2Deg;
                sockRest = sock.rotation;
                sockYaw = sockYaw0;
            }
            var list = new List<Renderer>();
            for (Transform n; (n = PezMotion.FindDeep(model, "approach_" + list.Count)) != null; )
                list.Add(n.GetComponentInChildren<Renderer>());
            rows = list.ToArray();
            var bt = PezMotion.FindDeep(model, "radar__M_E_Amber");
            beacon = bt ? bt.GetComponent<Renderer>() : null;
            phase = seed * 0.37f;
            radarYaw = seed * 47f;
        }

        public static AirfieldWorks For(GameObject model, PezMotion motion, int seed) => new AirfieldWorks(model.transform, motion, seed);

        /// <summary>The top of the lift deck where it is now (world).</summary>
        Vector3 Deck => lift ? lift.parent.TransformPoint(lift.localPosition + Vector3.up * DeckTop) : Vector3.zero;

        /// <summary>
        /// An aircraft was built here. If the lift is down the well, the aircraft rides it up: it's held on the deck (its
        /// spawn ease waits) until the deck is home, then lifts off. If the lift wasn't sent down (no warning: another
        /// airfield's queue, a fogged view), it just lifts off the deck. Returns where its spawn starts.
        /// </summary>
        public Vector3 Board(WorldView.EV aircraft)
        {
            after = 0f;
            if (motion && lowered) { motion.SetLiftUp(true, 0.7f); lowered = false; }
            rider = aircraft; riderT = 0f;
            return Deck;
        }

        /// <summary>Seconds until this airfield's next aircraft is done (infinity when it isn't building one).</summary>
        static float Due(World w, Entity e, out bool producing)
        {
            producing = false;
            var t = w.Teams[e.Team];
            if (!t.UnitQueues.TryGetValue(Producer.Airfield, out var q) || q.Count == 0) return float.PositiveInfinity;
            var item = q[0];
            var chosen = item.StructureId != 0 ? w.Get(item.StructureId) : null;
            var at = chosen != null && chosen.Team == e.Team && chosen.IsComplete && chosen.Def.Produces == Producer.Airfield ? chosen : w.FirstProducer(e.Team, Producer.Airfield);
            if (at != e) return float.PositiveInfinity;
            producing = true;
            var def = Defs.Get(item.Key);
            return def == null ? float.PositiveInfinity : (def.BuildTime - item.Progress) / (t.LowPower ? 0.5f : 1f);
        }

        public void Tick(World w, Entity e, bool sees, float dt)
        {
            float now = Time.time;
            bool low = w.Teams[e.Team].LowPower;
            float rate = low ? 0.5f : 1f;
            bool producing = false;
            float due = sees ? Due(w, e, out producing) : float.PositiveInfinity;
            after += dt;

            // The lift: down the well ahead of a launch (only while nothing's riding it), back up if the launch is off.
            if (motion && rider == null)
            {
                bool soon = due < Lead / rate;
                if (soon && !lowered) { motion.SetLiftUp(false, 1.2f); lowered = true; }
                else if (!soon && lowered) { motion.SetLiftUp(true, 1.2f); lowered = false; }
            }
            // The rider stays on the deck until it's home (or 2 s, whatever happens), then its spawn ease carries it off.
            if (rider != null)
            {
                riderT += dt;
                bool home = !lift || lift.localPosition.y >= liftRestY - 0.002f;
                if (home || riderT > 2f || rider.E.Dead || !rider.SpawnFrom.HasValue) { if (rider.SpawnT < 0f) rider.SpawnT = 0f; rider = null; }
                else { rider.SpawnFrom = Deck; rider.SpawnT = -1f; } // below 0 its ease holds at the start, whatever the frame time
            }

            // Approach lights: dark idle, a steady low glow while producing, a chase toward -Z around a launch.
            bool chase = sees && (due < 2f || after < 1.5f);
            float scale = low ? 0.5f : 1f;
            float sweep = Mathf.Repeat(now * (low ? 1f : 2f) + phase, 1f) * (rows.Length + 1f);
            for (int i = 0; i < rows.Length; i++)
            {
                if (!rows[i]) continue;
                float g = producing ? 0.4f * scale : 0.06f;
                if (chase)
                {
                    float d = sweep - i; // the lit row runs outward from the lift, with a short tail
                    g = d >= 0f && d < 1.4f ? Mathf.Lerp(1.6f, 0.25f, d / 1.4f) * scale : 0.12f;
                }
                PezShade.Set(rows[i], Mathf.Lerp(0.3f, 1f, Mathf.InverseLerp(0.06f, 1f, g)), g);
            }

            // Tower beacon and radar.
            if (beacon)
            {
                bool on = chase ? Mathf.Repeat(now * 2f + phase, 1f) < 0.35f : Mathf.Repeat(now / (low ? 3f : 1.5f) + phase, 1f) < 0.1f;
                float b = sees && on ? 1.6f * scale : 0.1f;
                PezShade.Set(beacon, b > 0.5f ? 1f : 0.4f, b);
            }
            if (radar)
            {
                radarSpeed = Mathf.MoveTowards(radarSpeed, (producing ? 120f : 40f) * rate, 80f * dt);
                radarYaw = Mathf.Repeat(radarYaw + radarSpeed * dt, 360f);
                radar.localRotation = radarRest * Quaternion.Euler(0f, radarYaw, 0f);
            }

            // Windsock: turns about the pole to point downwind, droops (about the horizontal) as the wind drops. Yaw and droop
            // are eased as angles, so it never takes a short cut through a twist.
            if (sock)
            {
                var wind = FxSystems.Wind;
                float s = Mathf.Clamp01(wind.magnitude / 0.6f);
                float yaw = wind.sqrMagnitude > 1e-6f ? Mathf.Atan2(wind.x, wind.z) * Mathf.Rad2Deg : sockYaw;
                float droop = Mathf.Lerp(65f, 10f, s);
                float k = 1f - Mathf.Exp(-dt * 2.5f);
                sockYaw = Mathf.LerpAngle(sockYaw, yaw, k);
                sockDroop = sockDroop < 0f ? droop : Mathf.Lerp(sockDroop, droop, k);
                float flutter = Mathf.Sin(now * 9f + phase) * 3f * s;
                var dir = Quaternion.Euler(0f, sockYaw + flutter, 0f) * Vector3.forward;
                var down = Quaternion.AngleAxis(sockDroop, Vector3.Cross(Vector3.up, dir)); // tips the sock's far end down
                sock.rotation = down * Quaternion.Euler(0f, sockYaw + flutter - sockYaw0, 0f) * sockRest;
            }
        }
    }
}
