// Drives the named animation nodes on a Pez model: turret, barrel, spinner, bin/bin_ore, door, lift.
// Add to the root of an imported .glb prefab. The profile is looked up from PezMotionProfiles by asset key.
// Gameplay code calls AimAt / ClearAim / Fire / SetWorking / SetDoorOpen / SetLiftUp / SetBinLoad / TipBin.
using UnityEngine;

namespace Pez
{
    [DisallowMultipleComponent]
    public class PezMotion : MonoBehaviour
    {
        [Tooltip("Asset key, e.g. light_tank. Defaults to the GameObject name.")]
        public string assetKey;
        public bool useGeneratedProfile = true;
        public PezMotionProfile profile = new PezMotionProfile();
        [Tooltip("Spinner runs at active rpm while true (harvesting, producing, under load).")]
        public bool working;
        public bool idleScan = true;
        [Tooltip("Idle scan half-angle (deg); heavy tanks scan narrower, artillery not at all.")]
        public float scanAmp = 40f;

        Transform turret, barrel, spinner, bin, binOre, door, lift, piston, mast, beam, oreTube;
        Vector3 barrelRestPos;
        Quaternion turretRest, binRest;
        float yaw, targetYaw, recoilT = -1f, rpm, doorScale = 1f, doorTarget = 1f;
        float liftRestY, liftOffset, liftTarget, binLoad, binShown, tipT = -1f;
        bool hasTarget;
        float scanPhase;
        // deep mining
        Vector3 pistonRest; Quaternion mastRest, beamRest;
        int thumpsLeft, thumpCount; float thumpT = -1f;
        float mastAngle, mastTarget, beamPhase, oreLevel = 1f, oreShown = 1f;
        public System.Action OnThump;   // spawn the ripple decal here
        public bool MastRaised => mastAngle <= -89f;

        public bool HasTurret => turret != null;

        void Awake()
        {
            if (string.IsNullOrEmpty(assetKey)) assetKey = name.Replace("(Clone)", "").Trim();
            if (useGeneratedProfile) profile = PezMotionProfiles.Get(assetKey);
            turret = FindDeep(transform, "turret");
            barrel = FindDeep(transform, "barrel");
            spinner = FindDeep(transform, "spinner");
            bin = FindDeep(transform, "bin");
            binOre = FindDeep(transform, "bin_ore");
            door = FindDeep(transform, "door");
            lift = FindDeep(transform, "lift");
            piston = FindDeep(transform, "piston");
            mast = FindDeep(transform, "mast");
            beam = FindDeep(transform, "beam");
            oreTube = FindDeep(transform, "ore_tube");
            if (piston) pistonRest = piston.localPosition;
            if (mast) mastRest = mast.localRotation;
            if (beam) beamRest = beam.localRotation;
            if (turret) turretRest = turret.localRotation;
            if (barrel) barrelRestPos = barrel.localPosition;
            twinL = FindDeep(transform, "barrel_l"); twinR = FindDeep(transform, "barrel_r");
            if (twinL) twinRestL = twinL.localPosition;
            if (twinR) twinRestR = twinR.localPosition;
            if (bin) binRest = bin.localRotation;
            if (lift) liftRestY = lift.localPosition.y;
            scanPhase = Random.value * 10f;
            if (binOre) binOre.localScale = new Vector3(1f, 0.001f, 1f);
        }

        // ---------------------------------------------------------------- public API
        public void AimAt(Vector3 worldPoint)
        {
            if (!turret) return;
            Vector3 local = turret.parent.InverseTransformPoint(worldPoint) - turret.localPosition;
            float a = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float lim = profile.turretYawLimit >= 360f ? 180f : profile.turretYawLimit;
            targetYaw = Mathf.Clamp(Mathf.DeltaAngle(0f, a), -lim, lim);
            hasTarget = true;
        }
        public void ClearAim() => hasTarget = false;
        public bool IsAimed(float toleranceDeg = 5f) => !turret || Mathf.Abs(Mathf.DeltaAngle(yaw, targetYaw)) <= toleranceDeg;
        public void Fire()
        {
            if (!barrel || profile.recoil <= 0f) return;
            if (twinL && twinR) { if ((NextTwin++ & 1) == 0) recoilTL = 0f; else recoilTR = 0f; } // one barrel per shot, L-R-L
            else recoilT = 0f;
        }
        /// <summary>Twin-barrel models (barrel_l / barrel_r, split from one mesh at load): which barrel fires next (even: L).</summary>
        public int NextTwin;
        public bool Twin => twinL && twinR;
        /// <summary>Artillery loading: the barrel slides back 0.06 along its axis and returns over 0.4 s (the ram).</summary>
        public void Ram() => ramT = 0f;
        Transform twinL, twinR;
        Vector3 twinRestL, twinRestR;
        float recoilTL = -1f, recoilTR = -1f, ramT = -1f;

        float Recoil(ref float t, float dt)
        {
            if (t < 0f) return 0f;
            t += dt;
            if (t < profile.recoilOut) return profile.recoil * (t / profile.recoilOut);
            float k = Mathf.Clamp01((t - profile.recoilOut) / profile.recoilReturn);
            if (k >= 1f) { t = -1f; return 0f; }
            return profile.recoil * Mathf.Pow(1f - k, 3f);
        }
        public void SetWorking(bool on) => working = on;
        /// <summary>Speed of the building's machinery (spinner, door, lift, beam): 0.5 on low power, as the sim halves
        /// production. Turrets and recoil are left alone: low power doesn't slow defences.</summary>
        public void SetRate(float r) => machineRate = r;

        // ---------------------------------------------------------------- crane (command center spinner)
        bool crane, craneHasTarget;
        Vector3 craneTarget, hookLocal;
        Quaternion craneRest;
        float craneYaw, jibAngle, craneIdlePhase;

        /// <summary>
        /// Command center: drive the spinner as a crane (MOTION.md aim_then_swing). With a target it slews its jib toward
        /// the site at 90 deg/s and swings +-5 deg at 0.4 Hz while it works; without one it idles in a +-10 deg sweep
        /// every 8 s. The jib's direction and the hook are read from the spinner's meshes once.
        /// </summary>
        public void SetCraneTarget(bool on, Vector3 world)
        {
            if (!spinner) return;
            if (!crane)
            {
                crane = true;
                craneRest = spinner.localRotation;
                var lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue); var hi = -lo;
                foreach (var mf in spinner.GetComponentsInChildren<MeshFilter>(true))
                {
                    var b = mf.sharedMesh.bounds;
                    for (int c = 0; c < 8; c++)
                    {
                        var p = spinner.InverseTransformPoint(mf.transform.TransformPoint(new Vector3((c & 1) == 0 ? b.min.x : b.max.x, (c & 2) == 0 ? b.min.y : b.max.y, (c & 4) == 0 ? b.min.z : b.max.z)));
                        lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
                    }
                }
                var mid = (lo + hi) * 0.5f;
                var jib = new Vector2(mid.x, mid.z);
                if (jib.sqrMagnitude < 1e-4f) jib = Vector2.up;
                jib.Normalize();
                jibAngle = Mathf.Atan2(jib.x, jib.y) * Mathf.Rad2Deg;
                // The hook hangs at the jib's far end, a third of the way down from its top.
                float reach = Mathf.Max(Mathf.Abs(jib.x) > 0.5f ? (jib.x > 0 ? hi.x : -lo.x) : (jib.y > 0 ? hi.z : -lo.z), 0.3f);
                hookLocal = new Vector3(jib.x * reach, Mathf.Lerp(hi.y, lo.y, 0.4f), jib.y * reach);
                craneIdlePhase = Random.value * 8f;
            }
            craneHasTarget = on;
            craneTarget = world;
        }

        /// <summary>The crane hook in world space (where the construction beam starts).</summary>
        public Vector3 CraneHook => spinner ? spinner.TransformPoint(hookLocal) : transform.position;

        void Crane(float dt)
        {
            float goal;
            if (craneHasTarget)
            {
                var local = spinner.parent.InverseTransformPoint(craneTarget) - spinner.localPosition;
                goal = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg - jibAngle + Mathf.Sin(Time.time * 0.4f * 6.2831853f) * 5f;
            }
            else goal = Mathf.Sin((Time.time + craneIdlePhase) * 6.2831853f / 8f) * 10f;
            craneYaw = Mathf.MoveTowardsAngle(craneYaw, goal, 90f * machineRate * dt);
            spinner.localRotation = Quaternion.Euler(0f, craneYaw, 0f) * craneRest;
        }
        float machineRate = 1f;
        public void SetDoorOpen(bool open) => doorTarget = open ? 0.05f : 1f;
        public bool DoorIsOpen => doorScale <= 0.06f;
        public void SetLiftUp(bool up) => liftTarget = up ? 0f : -0.6f;
        public void SnapLiftDown() { liftOffset = liftTarget = -0.6f; ApplyLift(); }
        public void SetBinLoad(float fraction) => binLoad = Mathf.Clamp01(fraction);
        public void TipBin() => tipT = 0f;
        /// <summary>Bay unloading: hold the bed tipped (35 deg, raised over 0.3 s) while true; lower it over 0.25 s after.
        /// The bed follows the sim's Dock steps (Unload raises it during the settle, PullOut lowers it).</summary>
        public void SetBinTipped(bool up) => tipGoal = up ? 1f : 0f;
        float tipK, tipGoal;
        /// <summary>Surveyor: thump `count` times, 1.2 s apart. OnThump fires on each slam.</summary>
        public void Survey(int count = 3) { thumpsLeft = thumpCount = count; thumpT = 0f; working = true; }
        public void CancelSurvey() { thumpsLeft = 0; thumpT = -1f; working = false; if (piston) piston.localPosition = pistonRest; }
        public bool Surveying => thumpsLeft > 0;
        /// <summary>Drill rig: raise or stow the mast (0 to -90 deg about X over 1.2 s).</summary>
        public void SetMastRaised(bool up) => mastTarget = up ? -90f : 0f;
        /// <summary>Deep mine: remaining reserve 0..1 drives the ore tube.</summary>
        public void SetOreLevel(float fraction) => oreLevel = Mathf.Clamp01(fraction);

        // ---------------------------------------------------------------- update
        void Update()
        {
            float dt = Time.deltaTime;

            if (turret && profile.turretYawSpeed > 0f)
            {
                float goal = hasTarget ? targetYaw
                    : idleScan ? Mathf.Sin((Time.time + scanPhase) * 0.35f) * Mathf.Min(scanAmp, profile.turretYawLimit) : 0f;
                yaw = Mathf.MoveTowardsAngle(yaw, goal, profile.turretYawSpeed * dt * (hasTarget ? 1f : 0.25f));
                turret.localRotation = turretRest * Quaternion.Euler(0f, yaw, 0f);
            }

            if (barrel && (recoilT >= 0f || ramT >= 0f))
            {
                float d = Recoil(ref recoilT, dt);
                if (ramT >= 0f) { ramT += dt; d += 0.06f * Mathf.Sin(Mathf.PI * Mathf.Clamp01(ramT / 0.4f)); if (ramT >= 0.4f) ramT = -1f; }
                barrel.localPosition = barrelRestPos + barrel.localRotation * Vector3.back * d;
            }
            if (twinL && recoilTL >= 0f) twinL.localPosition = twinRestL + Vector3.back * Recoil(ref recoilTL, dt);
            if (twinR && recoilTR >= 0f) twinR.localPosition = twinRestR + Vector3.back * Recoil(ref recoilTR, dt);

            if (spinner && crane) Crane(dt);
            else if (spinner)
            {
                float goalRpm = working ? profile.spinnerActiveRpm : profile.spinnerIdleRpm;
                float rate = Mathf.Max(profile.spinnerActiveRpm, profile.spinnerIdleRpm, 1f) / Mathf.Max(profile.spinupTime, 0.05f);
                rpm = Mathf.MoveTowards(rpm, goalRpm, rate * dt);
                Vector3 axis = profile.spinnerAxis == 'X' ? Vector3.right : profile.spinnerAxis == 'Z' ? Vector3.forward : Vector3.up;
                spinner.Rotate(axis, rpm * 6f * dt * machineRate, Space.Self);
            }

            if (door)
            {
                doorScale = Mathf.MoveTowards(doorScale, doorTarget, dt * machineRate / 0.35f);
                door.localScale = new Vector3(1f, doorScale, 1f);
            }

            if (lift)
            {
                liftOffset = Mathf.MoveTowards(liftOffset, liftTarget, dt * machineRate * (0.6f / 1.2f));
                ApplyLift();
            }

            if (binOre)
            {
                binShown = Mathf.MoveTowards(binShown, Mathf.Ceil(binLoad * 5f) / 5f, dt * 2f);
                binOre.localScale = new Vector3(1f, Mathf.Max(binShown, 0.001f), 1f);
            }
            if (piston && thumpsLeft > 0)
            {
                thumpT += dt;
                float c = thumpT % 1.2f;
                float y = c < 0.25f ? Mathf.SmoothStep(0f, 0.12f, c / 0.25f) : c < 0.31f ? Mathf.Lerp(0.12f, 0f, (c - 0.25f) / 0.06f) : 0f;
                piston.localPosition = pistonRest + Vector3.up * y;
                if (c >= 0.31f && c - dt < 0.31f) { OnThump?.Invoke(); }
                if (thumpT >= 1.2f * thumpCount) { thumpsLeft = 0; working = false; piston.localPosition = pistonRest; }
            }

            if (mast)
            {
                mastAngle = Mathf.MoveTowards(mastAngle, mastTarget, 90f / 1.2f * dt);
                float k = Mathf.Abs(mastAngle / 90f);
                float eased = -90f * (k * k * (3f - 2f * k));
                mast.localRotation = mastRest * Quaternion.Euler(eased, 0f, 0f);
            }

            if (beam && working)
            {
                beamPhase += dt * machineRate / 2.4f;
                beam.localRotation = beamRest * Quaternion.Euler(Mathf.Sin(beamPhase * 6.2831853f) * 18f, 0f, 0f);
            }

            if (oreTube)
            {
                oreShown = Mathf.MoveTowards(oreShown, oreLevel, dt / 1.5f);
                oreTube.localScale = new Vector3(1f, Mathf.Max(oreShown, 0.001f), 1f);
            }

            if (bin && tipT < 0f && tipK != tipGoal)
            {
                tipK = Mathf.MoveTowards(tipK, tipGoal, dt / (tipGoal > tipK ? 0.3f : 0.25f));
                bin.localRotation = binRest * Quaternion.Euler(-35f * Mathf.SmoothStep(0f, 1f, tipK), 0f, 0f);
            }
            if (bin && tipT >= 0f)
            {
                tipT += dt;
                float k = tipT / 1.2f;
                float tip = k < 0.4f ? Mathf.SmoothStep(0f, 35f, k / 0.4f) : k < 0.7f ? 35f : Mathf.SmoothStep(35f, 0f, (k - 0.7f) / 0.3f);
                bin.localRotation = binRest * Quaternion.Euler(-tip, 0f, 0f);
                if (k >= 0.55f) binLoad = 0f;
                if (k >= 1f) tipT = -1f;
            }
        }

        void ApplyLift()
        {
            var p = lift.localPosition; p.y = liftRestY + liftOffset; lift.localPosition = p;
        }

        public static Transform FindDeep(Transform root, string nodeName)
        {
            if (root.name == nodeName) return root;
            foreach (Transform c in root)
            {
                var r = FindDeep(c, nodeName);
                if (r) return r;
            }
            return null;
        }
    }
}
