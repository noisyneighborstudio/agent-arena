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
        public void Fire() { if (barrel && profile.recoil > 0f) recoilT = 0f; }
        public void SetWorking(bool on) => working = on;
        public void SetDoorOpen(bool open) => doorTarget = open ? 0.05f : 1f;
        public bool DoorIsOpen => doorScale <= 0.06f;
        public void SetLiftUp(bool up) => liftTarget = up ? 0f : -0.6f;
        public void SnapLiftDown() { liftOffset = liftTarget = -0.6f; ApplyLift(); }
        public void SetBinLoad(float fraction) => binLoad = Mathf.Clamp01(fraction);
        public void TipBin() => tipT = 0f;
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
                    : idleScan ? Mathf.Sin((Time.time + scanPhase) * 0.35f) * Mathf.Min(40f, profile.turretYawLimit) : 0f;
                yaw = Mathf.MoveTowardsAngle(yaw, goal, profile.turretYawSpeed * dt * (hasTarget ? 1f : 0.25f));
                turret.localRotation = turretRest * Quaternion.Euler(0f, yaw, 0f);
            }

            if (barrel && recoilT >= 0f)
            {
                recoilT += dt;
                float d;
                if (recoilT < profile.recoilOut) d = profile.recoil * (recoilT / profile.recoilOut);
                else
                {
                    float k = Mathf.Clamp01((recoilT - profile.recoilOut) / profile.recoilReturn);
                    d = profile.recoil * (1f - (1f - Mathf.Pow(1f - k, 3f)));
                    if (k >= 1f) recoilT = -1f;
                }
                barrel.localPosition = barrelRestPos + barrel.localRotation * Vector3.back * d;
            }

            if (spinner)
            {
                float goalRpm = working ? profile.spinnerActiveRpm : profile.spinnerIdleRpm;
                float rate = Mathf.Max(profile.spinnerActiveRpm, profile.spinnerIdleRpm, 1f) / Mathf.Max(profile.spinupTime, 0.05f);
                rpm = Mathf.MoveTowards(rpm, goalRpm, rate * dt);
                Vector3 axis = profile.spinnerAxis == 'X' ? Vector3.right : profile.spinnerAxis == 'Z' ? Vector3.forward : Vector3.up;
                spinner.Rotate(axis, rpm * 6f * dt, Space.Self);
            }

            if (door)
            {
                doorScale = Mathf.MoveTowards(doorScale, doorTarget, dt / 0.35f);
                door.localScale = new Vector3(1f, doorScale, 1f);
            }

            if (lift)
            {
                liftOffset = Mathf.MoveTowards(liftOffset, liftTarget, dt * (0.6f / 1.2f));
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
                beamPhase += dt / 2.4f;
                beam.localRotation = beamRest * Quaternion.Euler(Mathf.Sin(beamPhase * 6.2831853f) * 18f, 0f, 0f);
            }

            if (oreTube)
            {
                oreShown = Mathf.MoveTowards(oreShown, oreLevel, dt / 1.5f);
                oreTube.localScale = new Vector3(1f, Mathf.Max(oreShown, 0.001f), 1f);
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
