// Generated from motion.py. Values: tiles, seconds, degrees, rpm.
using System.Collections.Generic;

namespace Pez
{
    [System.Serializable]
    public class PezMotionProfile
    {
        public float turretYawSpeed, turretYawLimit = 360f, recoil, recoilOut = 0.06f, recoilReturn = 0.35f, barrelPitch;
        public char spinnerAxis = 'Y';
        public float spinnerIdleRpm, spinnerActiveRpm, spinupTime = 0.5f;
        public float maxSpeed, accel, turnRate;
        public bool turnInPlace;
    }

    public static class PezMotionProfiles
    {
        public static readonly Dictionary<string, PezMotionProfile> All = new Dictionary<string, PezMotionProfile>
        {
            {"command_center", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"outpost", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"power_plant", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"mining_refinery", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"barracks", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"factory", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"electronics_plant", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"optics_lab", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=20f, spinnerActiveRpm=60f, spinupTime=1.0f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"enrichment_plant", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"composite_foundry", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"fusion_reactor", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=45f, spinnerActiveRpm=120f, spinupTime=2.0f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"airfield", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"radar_dome", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=12f, spinnerActiveRpm=12f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"gun_turret", new PezMotionProfile { turretYawSpeed=120f, turretYawLimit=360f, recoil=0.1f, recoilOut=0.05f, recoilReturn=0.3f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"sam_site", new PezMotionProfile { turretYawSpeed=200f, turretYawLimit=360f, recoil=0.04f, recoilOut=0.03f, recoilReturn=0.15f, barrelPitch=-25f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"laser_tower", new PezMotionProfile { turretYawSpeed=180f, turretYawLimit=360f, recoil=0f, recoilOut=0.05f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"deep_mine", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=40f, spinupTime=1.5f, maxSpeed=0f, accel=0f, turnRate=0f, turnInPlace=false }},
            {"mining_truck", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='X', spinnerIdleRpm=0f, spinnerActiveRpm=240f, spinupTime=0.4f, maxSpeed=1.6f, accel=1.2f, turnRate=110f, turnInPlace=true }},
            {"repair_truck", new PezMotionProfile { turretYawSpeed=120f, turretYawLimit=360f, recoil=0f, recoilOut=0.05f, recoilReturn=0.35f, barrelPitch=-25f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=2.0f, accel=1.6f, turnRate=90f, turnInPlace=false }},
            {"outpost_truck", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=1.3f, accel=0.8f, turnRate=70f, turnInPlace=false }},
            {"scout_buggy", new PezMotionProfile { turretYawSpeed=300f, turretYawLimit=360f, recoil=0.03f, recoilOut=0.03f, recoilReturn=0.08f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=3.6f, accel=3.0f, turnRate=160f, turnInPlace=false }},
            {"light_tank", new PezMotionProfile { turretYawSpeed=150f, turretYawLimit=360f, recoil=0.12f, recoilOut=0.05f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=2.4f, accel=2.0f, turnRate=120f, turnInPlace=true }},
            {"heavy_tank", new PezMotionProfile { turretYawSpeed=80f, turretYawLimit=360f, recoil=0.16f, recoilOut=0.05f, recoilReturn=0.5f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=1.4f, accel=1.0f, turnRate=70f, turnInPlace=true }},
            {"artillery", new PezMotionProfile { turretYawSpeed=60f, turretYawLimit=360f, recoil=0.2f, recoilOut=0.05f, recoilReturn=0.6f, barrelPitch=-60f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=1.5f, accel=1.0f, turnRate=80f, turnInPlace=true }},
            {"long_range_artillery", new PezMotionProfile { turretYawSpeed=50f, turretYawLimit=360f, recoil=0.24f, recoilOut=0.05f, recoilReturn=0.7f, barrelPitch=-60f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=1.3f, accel=0.9f, turnRate=70f, turnInPlace=true }},
            {"laser_tank", new PezMotionProfile { turretYawSpeed=140f, turretYawLimit=360f, recoil=0f, recoilOut=0.05f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=1.8f, accel=1.4f, turnRate=100f, turnInPlace=true }},
            {"gunship", new PezMotionProfile { turretYawSpeed=220f, turretYawLimit=120f, recoil=0.02f, recoilOut=0.02f, recoilReturn=0.06f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=600f, spinnerActiveRpm=600f, spinupTime=1.0f, maxSpeed=3.0f, accel=2.0f, turnRate=140f, turnInPlace=true }},
            {"stealth_bomber", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=4.5f, accel=1.5f, turnRate=45f, turnInPlace=false }},
            {"rifleman", new PezMotionProfile { turretYawSpeed=400f, turretYawLimit=70f, recoil=0.02f, recoilOut=0.02f, recoilReturn=0.05f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=1.1f, accel=4.0f, turnRate=360f, turnInPlace=true }},
            {"rocket_soldier", new PezMotionProfile { turretYawSpeed=300f, turretYawLimit=70f, recoil=0.06f, recoilOut=0.05f, recoilReturn=0.25f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=0.9f, accel=3.5f, turnRate=360f, turnInPlace=true }},
            {"laser_trooper", new PezMotionProfile { turretYawSpeed=380f, turretYawLimit=70f, recoil=0f, recoilOut=0.05f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=1.0f, accel=4.0f, turnRate=360f, turnInPlace=true }},
            {"medic", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=1.2f, accel=4.0f, turnRate=360f, turnInPlace=true }},
            {"geological_surveyor", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=30f, spinnerActiveRpm=90f, spinupTime=0.6f, maxSpeed=2.2f, accel=1.8f, turnRate=110f, turnInPlace=false }},
            {"drill_rig", new PezMotionProfile { turretYawSpeed=0f, turretYawLimit=360f, recoil=0f, recoilOut=0.06f, recoilReturn=0.35f, barrelPitch=0f, spinnerAxis='Y', spinnerIdleRpm=0f, spinnerActiveRpm=0f, spinupTime=0.5f, maxSpeed=1.0f, accel=0.6f, turnRate=55f, turnInPlace=false }},
        };

        public static PezMotionProfile Get(string key) =>
            All.TryGetValue(key, out var p) ? p : new PezMotionProfile();
    }
}
