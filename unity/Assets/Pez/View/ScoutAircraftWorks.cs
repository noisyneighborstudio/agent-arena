using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>The scout family's guarded fans and survey head. WorldView alone owns root/Body flight;
    /// PezMotion has no generic spinner or turret nodes in these models. No unseen target is consulted.</summary>
    public sealed class ScoutAircraftWorks
    {
        readonly Transform[] rotors = new Transform[4];
        readonly Quaternion[] rotorRest = new Quaternion[4];
        readonly Transform sensorYaw, sensorPitch;
        readonly Quaternion yawRest, pitchRest;
        readonly float phase;
        float rotorAngle, rpm, yaw, pitch;

        ScoutAircraftWorks(GameObject model, string key, int seed)
        {
            string p = key == "long_range_drone" ? "lrd" : "rd";
            string[] sides = { "l_f", "r_f", "l_b", "r_b" };
            for (int i = 0; i < rotors.Length; i++)
            {
                rotors[i] = PezMotion.FindDeep(model.transform, p + "_fan_" + sides[i] + "_rotor");
                if (rotors[i]) rotorRest[i] = rotors[i].localRotation;
            }
            sensorYaw = PezMotion.FindDeep(model.transform, p + "_sensor_yaw");
            sensorPitch = PezMotion.FindDeep(model.transform, p + "_sensor_pitch");
            if (sensorYaw) yawRest = sensorYaw.localRotation;
            if (sensorPitch) pitchRest = sensorPitch.localRotation;
            phase = seed * 0.73f;
            // New airborne rigs start powered; a landed one eases to a stop on its first ticks.
            rpm = key == "long_range_drone" ? 450f : 540f;
        }

        public static ScoutAircraftWorks For(GameObject model, string key, int seed) =>
            new ScoutAircraftWorks(model, key, seed);

        public void Tick(Entity e, float time, float dt)
        {
            float cruise = e.Def.ModelKey == "long_range_drone" ? 450f : 540f;
            rpm = Mathf.MoveTowards(rpm, e.Landed ? 0f : cruise, cruise * dt / 0.8f);
            rotorAngle = Mathf.Repeat(rotorAngle + rpm * 6f * dt, 360f);
            // Diagonally paired fans counter-rotate. The shared guard protects the full swept circle.
            for (int i = 0; i < rotors.Length; i++)
                if (rotors[i]) rotors[i].localRotation = rotorRest[i] *
                    Quaternion.Euler(0f, rotorAngle * ((i == 0 || i == 3) ? 1f : -1f), 0f);

            // Inspect ground during travel; hover survey is a restrained smooth sweep, never target tracking.
            // A landed head settles level for service, and the actual Landed flag stops the flight machinery.
            float wantYaw = e.Landed ? 0f : e.Moving ? Mathf.Sin(time * 0.7f + phase) * 9f :
                Mathf.Sin(time * 0.4f + phase) * 42f;
            float wantPitch = e.Landed ? 0f : e.Moving ? 26f : 17f + Mathf.Sin(time * 0.55f + phase) * 8f;
            float follow = 1f - Mathf.Exp(-dt * 5f);
            yaw = Mathf.Lerp(yaw, wantYaw, follow);
            pitch = Mathf.Lerp(pitch, wantPitch, follow);
            if (sensorYaw) sensorYaw.localRotation = yawRest * Quaternion.Euler(0f, yaw, 0f);
            if (sensorPitch) sensorPitch.localRotation = pitchRest * Quaternion.Euler(pitch, 0f, 0f);
        }
    }
}
