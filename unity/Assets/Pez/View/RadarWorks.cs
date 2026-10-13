using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The radar dome's search antenna and beacon (MOTION.md radar_dome). The antenna (the model's spinner; its beam looks
    /// toward the feed marker on the horn) is driven here alone (PezMotionProfiles gives it 0 rpm):
    ///  - Powered (complete, the team not on low power): it sweeps clockwise at 12 rpm. When a new contact comes into this
    ///    dome's range (an enemy aircraft on radar within 28 tiles, or a stealth unit it has found within 16), it breaks off
    ///    and swings onto it (up to 180 deg/s), follows it for 1 s, then spins back up into the sweep (0.5 s).
    ///  - Low power (the sim switches the air search off): it coasts to a stop, then every 2.5 to 4.5 s lurches 20-30 deg
    ///    as the motor catches for a moment, and stalls again.
    ///  - Building: still. A render whose main view only remembers it under fog sweeps steadily (no live state).
    /// Tick returns the beacon's blink factor (WorldView multiplies the emissives' glow by it; the beacon is the dome's
    /// only emissive): a 0.12 s flash every second while sweeping, a quick double flash while it has contacts, dark on low
    /// power and while building.
    /// </summary>
    public class RadarWorks
    {
        const float SweepDps = 72f;   // 12 rpm
        const float SlewDps = 180f, HoldS = 1f, RangeAir = World.RadarRange, RangeStealth = 16f;
        readonly Transform spinner;
        readonly Quaternion rest;
        readonly float beam0, sense, phase;
        readonly HashSet<int> seen = new HashSet<int>(), now = new HashSet<int>();
        float yaw, vel, scanNext, lurchAt = -1f, lurchEnd = -1f, holdUntil = -1f;
        int target, contacts;
        bool primed;

        RadarWorks(Transform model, int seed)
        {
            spinner = PezMotion.FindDeep(model, "spinner");
            phase = seed * 0.37f;
            yaw = seed * 47f % 360f;
            if (!spinner) return;
            rest = spinner.localRotation;
            var feed = PezMotion.FindDeep(spinner, "feed");
            var p = spinner.parent;
            Vector3 b = feed ? p.InverseTransformPoint(feed.position) - spinner.localPosition : rest * Vector3.forward;
            beam0 = Mathf.Atan2(b.x, b.z) * Mathf.Rad2Deg;
            // Clockwise seen from above in the world, whatever mirroring the import put on the model.
            var s = p.lossyScale;
            sense = s.x * s.y * s.z < 0f ? -1f : 1f;
        }

        public static RadarWorks For(GameObject model, int seed) => new RadarWorks(model.transform, seed);

        /// <summary>Bearing (deg, in the spinner's parent space, relative to the beam at rest) of a point on the map.</summary>
        float Bearing(Vec2 at)
        {
            var w = new Vector3(at.X, spinner.position.y, at.Y);
            var l = spinner.parent.InverseTransformPoint(w) - spinner.localPosition;
            return Mathf.Atan2(l.x, l.z) * Mathf.Rad2Deg - beam0;
        }

        /// <summary>The contacts this dome holds: enemy aircraft on radar in its range, and stealth units it has found.</summary>
        void Scan(World w, Entity e)
        {
            now.Clear();
            foreach (var c in w.RadarContacts(e.Team))
                if (Vec2.Dist(e.Center, c.Pos) <= RangeAir) now.Add(c.Id);
            foreach (var id in w.Teams[e.Team].Detected)
                if (w.Get(id) is { IsMine: false } s && Vec2.Dist(e.Center, s.Pos) <= RangeStealth) now.Add(id);
            contacts = now.Count;
            if (target == 0 || !now.Contains(target))
                foreach (var id in now)
                    if (!seen.Contains(id)) { if (primed) { target = id; holdUntil = -1f; } break; } // a new one: go and look
            seen.Clear(); seen.UnionWith(now);
            primed = true;
        }

        /// <summary>Drive the antenna; returns the beacon's blink factor.</summary>
        public float Tick(World w, Entity e, bool live, float dt)
        {
            if (!spinner) return 1f;
            float t = Time.time;
            bool low = w.Teams[e.Team].LowPower, on = e.IsComplete && !low;
            if (!live) { on = e.IsComplete; low = false; target = 0; contacts = 0; }
            else if (on && t >= scanNext) { Scan(w, e); scanNext = t + 0.25f; }
            if (!on) { target = 0; contacts = 0; primed = false; }

            var tgt = target != 0 ? w.Get(target) : null;
            if (target != 0 && tgt == null) target = 0;
            if (tgt != null)
            {
                // Swing onto the contact and follow it (a servo: fast but eased, no snap), then hold for a second.
                float err = Mathf.DeltaAngle(yaw, Bearing(tgt.Pos));
                float want = Mathf.Clamp(err * 6f, -SlewDps, SlewDps);
                vel = Mathf.MoveTowards(vel, want, 720f * dt);
                if (holdUntil < 0f && Mathf.Abs(err) < 2f) holdUntil = t + HoldS;
                if (holdUntil >= 0f && t >= holdUntil) { target = 0; holdUntil = -1f; }
            }
            else if (on) vel = Mathf.MoveTowards(vel, SweepDps * sense, SweepDps / 0.5f * dt);
            else if (low && e.IsComplete)
            {
                // Starved: coast down, and now and then the motor catches and throws it round a little way.
                if (lurchAt < 0f) lurchAt = t + Random.Range(1.5f, 3f);
                if (t >= lurchAt) { lurchEnd = t + Random.Range(0.25f, 0.35f); lurchAt = t + Random.Range(2.5f, 4.5f); }
                vel = t < lurchEnd ? Mathf.MoveTowards(vel, 70f * sense, 600f * dt) : Mathf.MoveTowards(vel, 0f, 200f * dt);
            }
            else vel = Mathf.MoveTowards(vel, 0f, 200f * dt);
            if (on) lurchAt = -1f;
            yaw = Mathf.Repeat(yaw + vel * dt, 360f);
            spinner.localRotation = Quaternion.Euler(0f, yaw, 0f) * rest;

            if (!live) return 1f;
            if (!on) return 0.12f;
            float k = Mathf.Repeat(t + phase, contacts > 0 ? 0.6f : 1f);
            bool lit = contacts > 0 ? k < 0.08f || (k > 0.16f && k < 0.24f) : k < 0.12f;
            return lit ? 1.6f : 0.12f;
        }
    }
}
