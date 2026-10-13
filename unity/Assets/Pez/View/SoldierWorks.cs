using Pez.Sim;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// A candy trooper's upper body (build_models.candy_trooper: the rifleman and the soldiers built on it), from the sim:
    ///  - arms: authored lowered (the weapon at a low ready). The soldier brings it up level when it has something in
    ///    range to shoot (the sim's LastFiredAt: it's tracking a target), in 0.18 s, and holds it there 2.5 s after its last
    ///    shot before lowering it over 0.45 s. Each shot kicks it up (the muzzle climbs 9 degrees and settles in ~0.2 s)
    ///    while PezMotion slides the weapon back along the barrel.
    ///  - head: idle, it glances about (one side, back to the front, now and then the other), a slow look that eases in and
    ///    out; walking it mostly looks where it's going; with the weapon up it looks down the sights, cheek to the stock.
    /// The rest is elsewhere, one system per transform: Gait walks the legs and sways the hips (the idle weight shift),
    /// PezMotion yaws the torso (turret) to aim and recoils the weapon (barrel). Per frame: two rotations, no allocations.
    /// </summary>
    public class SoldierWorks
    {
        const float Up = 0.18f, Down = 0.45f, Hold = 2.5f, KickDeg = 9f;

        readonly Transform arms, head;
        readonly Quaternion armsRest, headRest;
        float raise, kick, seenShot = -99f;
        float yaw, pitch, roll, goal, nextLook;

        SoldierWorks(Transform model, int seed)
        {
            arms = PezMotion.FindDeep(model, "arms");
            head = PezMotion.FindDeep(model, "head");
            if (arms) armsRest = arms.localRotation;
            if (head) headRest = head.localRotation;
            nextLook = Time.time + 1.5f + (seed * 0.618034f) % 1f * 3f;
        }

        /// <summary>A trooper's works; a model without the nodes (older infantry) gets one that does nothing.</summary>
        public static SoldierWorks For(GameObject model, int seed) => new SoldierWorks(model.transform, seed);

        /// <param name="simTime">The sim's clock (World.Time), for the entity's LastFiredAt.</param>
        /// <param name="lastShot">When the view last played one of its shots (Time.time).</param>
        public void Tick(Entity e, float simTime, float lastShot, float dt)
        {
            if (dt <= 0f) return;
            bool shot = lastShot > seenShot + 1e-4f;
            seenShot = lastShot;
            bool tracking = simTime - e.LastFiredAt < 0.6f;   // FireAt runs every tick it has a target in range
            bool up = tracking || Time.time - lastShot < Hold || (e.Order == Order.Attack && !e.Moving);
            raise = Mathf.MoveTowards(raise, up ? 1f : 0f, dt / (up ? Up : Down));
            if (shot) kick = Mathf.Min(kick + KickDeg, KickDeg * 1.4f);
            kick *= Mathf.Exp(-dt * 14f);
            float k = raise * raise * (3f - 2f * raise);
            if (arms) arms.localRotation = Quaternion.Slerp(armsRest, Quaternion.identity, k) * Quaternion.Euler(-kick, 0f, 0f);

            if (!head) return;
            float now = Time.time;
            if (now >= nextLook)
            {
                // Mostly front, then a look to one side; walking, only small ones.
                bool side = Mathf.Abs(goal) < 1f && Random.value < 0.75f;
                float amp = e.Moving ? 18f : 42f;
                goal = side ? Random.Range(0.45f, 1f) * amp * (Random.value < 0.5f ? -1f : 1f) : 0f;
                nextLook = now + (side ? Random.Range(1.2f, 2.4f) : Random.Range(2f, 5f));
            }
            // Weapon up: eyes down the sights (head tipped 7 degrees, cheek on the stock).
            float g = Mathf.Lerp(goal, 0f, k);
            float s = 1f - Mathf.Exp(-dt * 5f);
            yaw = Mathf.Lerp(yaw, g, s);
            pitch = Mathf.Lerp(pitch, 7f * k + kick * 0.3f, 1f - Mathf.Exp(-dt * 10f));
            roll = Mathf.Lerp(roll, 8f * k, 1f - Mathf.Exp(-dt * 10f));
            head.localRotation = headRest * Quaternion.Euler(pitch, yaw, roll);
        }
    }
}
