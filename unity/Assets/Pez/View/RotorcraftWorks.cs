using System.Collections.Generic;
using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The two candy rotorcraft acts. WorldView keeps Root/Body/altitude/spawn/landing;
    /// PezMotion keeps spinner/spinner_1 and the gunship's generic barrel recoil.
    /// This class alone owns tc_door_*, tc_hoist_line, tc_seat_* and gs_round_*/gs_tail_rotor.
    /// Transport load/unload is airborne in the sim. Actual full-size troopers follow a bounded
    /// view-only hoist path, disappearing through its real doorway, without a gameplay landing delay.
    /// Private passenger hints and reload state are normalized per render and before a shared wreck.
    /// </summary>
    public sealed class RotorcraftWorks
    {
        public const float HoistSeconds = 1f, SeatInterval = .22f;
        readonly Rig rig;
        readonly Transform bay, hoistTop, cable, tailRotor;
        readonly Transform[] doors = new Transform[2], seats = new Transform[6], rounds = new Transform[2], mouths = new Transform[2], blasts = new Transform[2];
        readonly Quaternion[] doorRest = new Quaternion[2];
        readonly Quaternion tailRest;
        readonly Vector3[] roundRest = new Vector3[2];
        readonly Renderer[][] seatRenderers = new Renderer[6][];
        float doorK, until, tailAngle, pulseAt = -99f;
        readonly float cooldown;
        int prevPax, leaving, nextPod, firingPod;
        float unloadAt = -99f;
        bool owner, wreck;
        readonly bool carrier;
        readonly bool[] occupied = new bool[6];

        RotorcraftWorks(Rig r, EntityDef def)
        {
            rig = r; carrier = def.ModelKey == "transport_chopper";
            var m = r.Model.transform;
            bay = PezMotion.FindDeep(m, "tc_bay");
            hoistTop = PezMotion.FindDeep(m, "tc_hoist_top");
            cable = PezMotion.FindDeep(m, "tc_hoist_line");
            tailRotor = PezMotion.FindDeep(m, "gs_tail_rotor");
            if (tailRotor) tailRest = tailRotor.localRotation;
            for (int i = 0; i < 2; i++)
            {
                doors[i] = PezMotion.FindDeep(m, "tc_door_" + i);
                if (doors[i]) doorRest[i] = doors[i].localRotation;
                rounds[i] = PezMotion.FindDeep(m, "gs_round_" + i);
                if (rounds[i]) roundRest[i] = rounds[i].localPosition;
                mouths[i] = PezMotion.FindDeep(m, "gs_muzzle_" + i);
                blasts[i] = PezMotion.FindDeep(m, "gs_backblast_" + i);
            }
            for (int i = 0; i < 6; i++)
            {
                seats[i] = PezMotion.FindDeep(m, "tc_seat_" + i);
                if (seats[i]) seatRenderers[i] = seats[i].GetComponentsInChildren<Renderer>(true);
            }
            cooldown = def.Weapon != null ? def.Weapon.Cooldown : 1.2f;
            Show(false); // snapshot/fog startup never flashes six private occupants.
        }

        public static RotorcraftWorks For(Rig rig, EntityDef def) => rig.Model != null &&
            (def.ModelKey == "transport_chopper" || def.ModelKey == "gunship") ? new RotorcraftWorks(rig, def) : null;
        public bool HasBay => !wreck && carrier && bay && hoistTop;
        public static float ExitDelay(int seat) => .08f + Mathf.Clamp(seat, 0, 5) * SeatInterval;
        public static float BoardDuration(int seat) => HoistSeconds + ExitDelay(seat);
        public static float BoardPhase(float elapsed, int seat) => Mathf.Clamp01((elapsed - ExitDelay(seat)) / HoistSeconds);

        /// <summary>Ground approach, vertical hoist outside the stern, then through the unobstructed rear aperture.</summary>
        public List<Vector3> BayPath(Vector3 outside)
        {
            var path = new List<Vector3> { outside };
            if (!HasBay) { path.Add(rig.Root.position); return path; }
            var entry = bay.position;
            var rear = entry - rig.Body.forward * .66f;
            var ground = new Vector3(rear.x, outside.y, rear.z);
            if (Vector3.Distance(outside, ground) > .01f) path.Add(ground);
            path.Add(rear);
            path.Add(entry);
            return path;
        }

        /// <summary>A real soldier crosses the doorway; hold it open until the full-size handoff completes.</summary>
        public void Pass() => until = Time.time + .6f;

        public void Tick(WorldView view, WorldView.EV v, float dt)
        {
            if (wreck) return;
            var e = v.E;
            if (tailRotor)
            {
                // Separate anti-torque rotor: visually sampled at at most 36deg/frame at 30fps, no reverse strobe.
                tailAngle = Mathf.Repeat(tailAngle + Mathf.Min(dt * (e.Landed ? 0f : 900f), 36f), 360f);
                tailRotor.localRotation = tailRest * Quaternion.Euler(0f, tailAngle, 0f);
            }
            if (HasBay)
            {
                int pax = e.Passengers.Count;
                if (pax < prevPax) { leaving = prevPax; unloadAt = Time.time; until = Time.time + HoistSeconds + ExitDelay(leaving - 1) + .45f; }
                prevPax = pax;
                bool loading = false;
                for (int i = 0; i < 6; i++) occupied[i] = false;
                for (int i = 0; i < Mathf.Min(6, pax); i++)
                {
                    bool arrived = !view.Views.TryGetValue(e.Passengers[i], out var pv) || pv.BoardT >= 1f;
                    occupied[i] = arrived;
                    if (!arrived) loading = true;
                }
                if (!loading)
                    foreach (var u in view.World.Entities)
                        if (!u.Dead && u.Team == e.Team && u.Order == Order.Board && u.TargetId == e.Id && Vec2.Dist(u.Pos, e.Pos) < e.Def.Radius + 2.2f) { loading = true; break; }
                for (int i = 0; i < leaving && i < 6; i++)
                    occupied[i] |= Time.time - unloadAt < ExitDelay(i);
                if (loading) until = Time.time + .55f;
                doorK = Mathf.MoveTowards(doorK, loading || Time.time < until ? 1f : 0f, dt / .22f);
            }
            Show(view.PovTeam < 0 || e.Team == view.PovTeam);
        }

        public void Show(bool own)
        {
            owner = own && !wreck;
            for (int i = 0; i < 6; i++)
                if (seatRenderers[i] != null) foreach (var r in seatRenderers[i]) if (r) r.enabled = owner && occupied[i];
            float open = owner ? doorK : 0f;
            for (int i = 0; i < 2; i++)
                if (doors[i]) { doors[i].localRotation = doorRest[i]; doors[i].localScale = new Vector3(1f, Mathf.Lerp(1f, .02f, Mathf.SmoothStep(0f, 1f, open)), 1f); }
            if (cable)
            {
                cable.gameObject.SetActive(owner && open > .03f);
                // The rope always ends at the ground underneath its real attachment, including banked aircraft.
                var end = bay.position - rig.Body.forward * .66f; end.y = 0f;
                var down = end - hoistTop.position;
                float length = Mathf.Max(.01f, down.magnitude);
                cable.localRotation = Quaternion.FromToRotation(Vector3.down, cable.parent.InverseTransformDirection(down.normalized));
                cable.localScale = new Vector3(1f, length * open, 1f);
            }
            for (int i = 0; i < 2; i++)
                if (rounds[i])
                {
                    float age = Time.time - pulseAt;
                    bool ready = !owner || i != firingPod || age >= cooldown;
                    rounds[i].gameObject.SetActive(!wreck && (ready || age >= cooldown * .64f));
                    float feed = ready ? 0f : Mathf.Lerp(-.10f, 0f, Mathf.Clamp01((age - cooldown * .64f) / (cooldown * .36f)));
                    rounds[i].localPosition = roundRest[i] + Vector3.forward * feed;
                }
        }

        /// <summary>Select the next real cartridge mouth BEFORE projectile/flashes read MuzzleOf. Does not consume a shot.</summary>
        public void ReadyShot(Rig r)
        {
            if (wreck || carrier || !r.Barrel || !mouths[nextPod]) return;
            r.HasMuzzle = true; r.Twin = false;
            r.MuzzleLocal = r.Barrel.InverseTransformPoint(mouths[nextPod].position);
        }

        /// <summary>Called exactly once per sim fire event; no invented salvo or second projectile.</summary>
        public void Fired(WorldView view, WorldView.EV v)
        {
            if (wreck || carrier) return;
            firingPod = nextPod; nextPod = 1 - nextPod; pulseAt = Time.time;
            // The fire-event caller already set its audience. Reusing it avoids mutating WorldView's shared audience list.
            if (blasts[firingPod]) Fx.Backblast(blasts[firingPod].position, -blasts[firingPod].forward, .4f);
            Show(owner);
        }

        /// <summary>Call before detaching aircraft model: occupants/rope gone, doors shut, no private reload state.</summary>
        public void Wreck()
        {
            wreck = true; doorK = 0f;
            for (int i = 0; i < 6; i++) occupied[i] = false;
            Show(false);
        }
    }
}
