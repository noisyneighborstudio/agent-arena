using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>Animates a transient line effect (tracers, beams): fade and self-destruct.</summary>
    public class FxLife : MonoBehaviour
    {
        public float Life = 0.5f;
        public Material Mat;
        public Color Color;
        float age;

        void Update()
        {
            age += Time.deltaTime;
            float t = age / Life;
            if (t >= 1f) { Destroy(gameObject); if (Mat != null) Destroy(Mat); return; }
            if (Mat != null) { var c = Color; c.a *= 1f - t; Mat.color = c; }
        }
    }

    /// <summary>
    /// Battle effects. Explosions are particle physics (see <see cref="FxSystems"/>): a flash and a point light, a fireball
    /// of hot gas that expands, rises and cools white to yellow to orange to red to soot, lit smoke that billows up and
    /// spreads, sparks flying ballistically and bouncing, faceted debris in the unit's colours that tumbles and settles,
    /// a dust shockwave along the ground and a scorch mark that fades. Each event has its own recipe and scale.
    /// Stylised palette (warm sugar-dust smoke #3A3026, biscuit ground, cream plastic), physical motion.
    /// </summary>
    public static class Fx
    {
        static FxSystems S => FxSystems.I;

        public static readonly Color32 White = new Color32(255, 255, 255, 255);
        static readonly Color32 FlashCol = new Color32(255, 246, 222, 255);
        static readonly Color FlashLight = new Color(1f, 0.62f, 0.28f);
        static readonly Color32 SmokeWarm = new Color32(58, 48, 38, 255);   // #3A3026 warm sugar-dust smoke
        static readonly Color32 SmokeAsh = new Color32(96, 84, 70, 255);
        static readonly Color32 GunSmoke = new Color32(150, 142, 128, 255);
        static readonly Color32 DustPale = PezPalette.MaterialsSugarPad;     // #B9AE98
        static readonly Color32 DustBiscuit = PezPalette.TerrainBiscuitLight;
        static readonly Color32 Clod = PezPalette.TerrainBiscuitDark;
        static readonly Color32 Cream = PezPalette.MaterialsCreamPlastic;
        static readonly Color32 Hull = PezPalette.MaterialsSmokePlastic;
        static readonly Color32 Steel = PezPalette.MaterialsSpringSteel;
        static readonly Color32 Licorice = PezPalette.MaterialsLicorice;
        static readonly Color32 ScorchCol = new Color32(24, 19, 17, 215);
        static readonly Vector3 Up = Vector3.up;
        /// <summary>How long a vehicle wreck smokes (s); the wreck lies 20 s.</summary>
        public const float WreckSmoke = 12f;

        /// <summary>Build the particle systems now (not mid-battle) and draw each once so its shader is ready.</summary>
        public static void Prewarm()
        {
            var cam = Camera.main;
            S.Prewarm(cam != null ? cam.transform.position + cam.transform.forward * 50f : new Vector3(0, 0.5f, 0));
        }

        // ---------------------------------------------------------------- helpers

        static void Emit(ParticleSystem ps, Vector3 pos, Vector3 vel, float size, float life, Color32 c, float rot = 0f)
        {
            if (Audience == null) { FxSystems.Emit(ps, pos, vel, size, life, c, rot); return; }
            foreach (var layer in Audience) FxSystems.Emit(S.OnLayer(ps, layer), pos, vel, size, life, c, rot);
        }

        /// <summary>
        /// While set, effects go only to these render layers (one per camera audience) instead of every camera. A
        /// building's state effects (damage smoke and fire, power-plant steam) are emitted this way, so a player's stream
        /// only shows them when that player can actually see the building (they'd otherwise rise out of the fog over a
        /// remembered enemy structure and give its state away). Set and cleared around the call by WorldView.
        /// </summary>
        public static List<int> Audience;

        static Color32 Vary(Color32 c, float amount, byte alpha = 255)
        {
            float k = 1f + Random.Range(-amount, amount);
            return new Color32((byte)Mathf.Min(255f, c.r * k), (byte)Mathf.Min(255f, c.g * k), (byte)Mathf.Min(255f, c.b * k), alpha);
        }

        static Color32 Alpha(Color32 c, float a) { c.a = (byte)(Mathf.Clamp01(a) * 255f); return c; }

        public static Color32 SmokeColor(float alpha) => Vary(Color32.Lerp(SmokeWarm, SmokeAsh, Random.value * 0.6f), 0.1f, (byte)(alpha * 255f));

        static Vector3 Ground(Vector3 p) => new Vector3(p.x, 0f, p.z);

        static Vector3 Flat(float speed)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            return new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * speed;
        }

        /// <summary>Faceted debris: half in `a`, 30% `b`, 20% `c`; `hot` is the share that glows as it flies.</summary>
        static void Chunks(Vector3 pos, int n, float size, float speed, float life, Color32 a, Color32 b, Color32 c, float hot, float lift = 1.2f)
        {
            for (int i = 0; i < n; i++)
            {
                var dir = Random.onUnitSphere;
                dir.y = Mathf.Abs(dir.y) + lift * Random.Range(0.3f, 1f);
                dir.Normalize();
                float r = Random.value;
                var col = Vary(r < 0.5f ? a : r < 0.8f ? b : c, 0.12f, (byte)(Random.value < hot ? 255 : 0));
                S.EmitShard(pos + Random.insideUnitSphere * 0.15f * size, dir * speed * Random.Range(0.45f, 1f),
                    Random.Range(0.06f, 0.13f) * size, life * Random.Range(0.8f, 1.25f), col);
            }
        }

        // ---------------------------------------------------------------- building blocks

        /// <summary>
        /// One explosion of scale `s` (1 = a vehicle): flash, light, fireball, smoke, sparks, and when it's on the ground
        /// a dust shockwave. Ground bursts throw everything into the upper hemisphere; air bursts go every way.
        /// </summary>
        public static void Blast(Vector3 pos, float s, bool ground)
        {
            var S = FxSystems.I;
            float sq = Mathf.Sqrt(s);
            Emit(S.Flash, pos + Up * 0.1f * s, Vector3.zero, 2f * s, 0.09f + 0.05f * s, FlashCol);
            S.Lamp(pos + Up * (0.4f + 0.4f * s), FlashLight, 1.2f + 2f * s, 1.5f + 3.5f * s, 0.15f + 0.15f * s);

            int nf = Mathf.Clamp((int)(8 + 22 * s), 6, 50);
            for (int i = 0; i < nf; i++)
            {
                var dir = Random.onUnitSphere;
                if (ground) dir.y = Mathf.Abs(dir.y) * 0.9f + 0.1f;
                Emit(S.Fire, pos + dir * 0.12f * s, dir * Random.Range(1.2f, 4.2f) * sq + Up * Random.Range(0f, 0.6f) * sq,
                    Random.Range(0.5f, 0.95f) * s, Random.Range(0.55f, 1.05f) * (0.75f + 0.25f * sq), Vary(White, 0.04f));
            }

            int ns = Mathf.Clamp((int)(4 + 12 * s), 3, 36);
            for (int i = 0; i < ns; i++)
            {
                var dir = Random.onUnitSphere;
                if (ground) dir.y = Mathf.Abs(dir.y);
                Emit(S.Smoke, pos + dir * 0.2f * s, dir * Random.Range(0.4f, 1.6f) * sq + Up * Random.Range(0.5f, 1.2f) * sq,
                    Random.Range(0.55f, 1f) * s, Random.Range(2.4f, 4.2f) * (0.8f + 0.2f * sq), SmokeColor(0.92f));
            }

            int nk = Mathf.Clamp((int)(10 + 34 * s), 4, 90);
            for (int i = 0; i < nk; i++)
            {
                var dir = Random.onUnitSphere;
                dir.y = ground ? Mathf.Abs(dir.y) * 1.1f + 0.15f : dir.y + 0.3f;
                Emit(S.Sparks, pos + dir * 0.1f * s, dir * Random.Range(2.5f, 8.5f) * sq, Random.Range(0.035f, 0.065f) * (0.8f + 0.2f * sq),
                    Random.Range(0.5f, 1.5f), White);
            }

            if (ground) Shockwave(Ground(pos), s);
        }

        /// <summary>The blast wave along the ground: a dust ring racing out and dust kicked sideways.</summary>
        static void Shockwave(Vector3 g, float s)
        {
            float sq = Mathf.Sqrt(s);
            Emit(S.Ring, g + Up * 0.05f, Vector3.zero, 3.4f * s, 0.4f + 0.25f * s, Alpha(Color32.Lerp(DustPale, Cream, 0.5f), 0.8f), Random.Range(0f, 360f));
            int nd = Mathf.Clamp((int)(4 + 10 * s), 3, 30);
            for (int i = 0; i < nd; i++)
            {
                var dir = Flat(1f);
                Emit(S.Dust, g + dir * 0.3f * s + Up * 0.15f * s, dir * Random.Range(1.5f, 3.5f) * sq + Up * Random.Range(0.1f, 0.5f),
                    Random.Range(0.45f, 0.8f) * s, Random.Range(1.6f, 3f), Vary(Color32.Lerp(DustBiscuit, DustPale, Random.value), 0.06f, 200));
            }
        }

        /// <summary>A dark blotch burnt into the ground; stays a while (the battle's history), fading over 75 s.</summary>
        public static void Scorch(Vector3 pos, float size) =>
            Emit(S.Scorch, new Vector3(pos.x, 0.035f, pos.z), Vector3.zero, size * Random.Range(0.9f, 1.1f), 75f, ScorchCol, Random.Range(0f, 360f));

        /// <summary>Line effects (beams, tracers) are additive: lift them above the bloom threshold so they glow.</summary>
        static Color Hot(Color c, float k) => new Color(c.r * k, c.g * k, c.b * k, c.a);

        /// <summary>A plain explosion of the given scale (C4 charges and anything without its own recipe).</summary>
        public static void Explosion(Vector3 pos, float size) => Blast(pos, size, pos.y < 0.9f);

        /// <summary>Thrown debris in one colour (kept for callers that just want bits).</summary>
        public static void Debris(Vector3 pos, float size, Color c, int n) => Chunks(pos + Up * 0.2f, n, size, 3.5f, 3f, c, Hull, Licorice, 0f);

        // ---------------------------------------------------------------- event recipes

        /// <summary>A projectile arrives: scale and character by weapon.</summary>
        public static void Hit(string weapon, Vector3 pos)
        {
            bool ground = pos.y < 0.6f;
            switch (weapon)
            {
                case "bombs": Shell(pos, 1.5f); break;
                case "artillery": Shell(pos, 1.05f); break;
                case "mammoth_cannon": Shell(pos, 0.8f); break;
                case "heavy_cannon": Shell(pos, 0.6f); break;
                case "mine": Mine(pos); break;
                case "flak":
                    // Flak bursts in the air and leaves a hanging black puff.
                    Blast(pos, 0.35f, ground);
                    for (int i = 0; i < 3; i++)
                        Emit(S.Smoke, pos + Random.insideUnitSphere * 0.15f, Random.insideUnitSphere * 0.4f, Random.Range(0.35f, 0.5f), Random.Range(2f, 3f), SmokeColor(0.9f));
                    break;
                case "rocket": case "gunship_rockets": case "sam": Blast(pos, 0.42f, ground); break;
                default: Blast(pos, 0.3f, ground); break; // cannon, turret gun
            }
        }

        /// <summary>A shell or bomb: a ground burst that throws dirt clods and a dust column, and scorches.</summary>
        static void Shell(Vector3 pos, float s)
        {
            bool ground = pos.y < 0.6f;
            Blast(pos, s, ground);
            if (!ground) return;
            var g = Ground(pos);
            float sq = Mathf.Sqrt(s);
            Chunks(g + Up * 0.1f, (int)(4 + 8 * s), 0.9f * s, 4f * sq, 3.5f, Clod, DustBiscuit, Licorice, 0f, 2f);
            int nd = (int)(3 + 5 * s);
            for (int i = 0; i < nd; i++)
                Emit(S.Dust, g + Flat(0.25f * s) + Up * 0.2f, Up * Random.Range(1.5f, 3.2f) * sq + Flat(0.5f), Random.Range(0.4f, 0.7f) * s,
                    Random.Range(2f, 3.2f), Vary(DustBiscuit, 0.08f, 210));
            Scorch(g, 1.4f * s);
        }

        /// <summary>A hitscan round (rifle, machine gun, sniper) lands: a spit of sparks and a little dust.</summary>
        public static void BulletImpact(Vector3 pos)
        {
            Emit(S.Flash, pos, Vector3.zero, 0.22f, 0.05f, FlashCol);
            for (int i = 0; i < 3; i++)
                Emit(S.Sparks, pos, (Random.onUnitSphere + Up * 0.8f) * Random.Range(1.5f, 4f), 0.025f, Random.Range(0.2f, 0.45f), White);
            Emit(S.Dust, pos, Up * 0.3f + Random.insideUnitSphere * 0.2f, Random.Range(0.14f, 0.22f), Random.Range(0.6f, 0.9f), Alpha(DustPale, 0.6f));
        }

        /// <summary>A gun fires: muzzle flash, a short light, and a puff of gun smoke. Tiny ones (welders) spit sparks.</summary>
        public static void MuzzleFlash(Vector3 pos, float size)
        {
            Emit(S.Flash, pos, Vector3.zero, size * 4.5f, 0.07f, FlashCol);
            if (size >= 0.1f) S.Lamp(pos, FlashLight, 1f + size * 5f, 1.5f + size * 6f, 0.08f);
            Emit(S.Dust, pos, Up * 0.25f + Random.insideUnitSphere * 0.1f, size * 3f, Random.Range(0.6f, 1.1f), Alpha(GunSmoke, 0.45f));
            if (size < 0.08f)
                for (int i = 0; i < 3; i++)
                    Emit(S.Sparks, pos, (Random.onUnitSphere + Up * 0.5f) * Random.Range(1f, 2.5f), 0.02f, Random.Range(0.25f, 0.5f), White);
        }

        /// <summary>A vehicle blows up: blast, a secondary cook-off, its hull in pieces, and a wreck that smokes for 12 s
        /// (flames for the first few seconds). The wreck itself darkens and stays 20 s (PezEmerge.PlayRemove).</summary>
        public static void VehicleDestroyed(Vector3 pos, Color team)
        {
            bool ground = pos.y < 0.9f;
            Blast(pos, 1f, ground);
            S.Later(Random.Range(0.18f, 0.35f), pos + new Vector3(Random.Range(-0.3f, 0.3f), 0.1f, Random.Range(-0.3f, 0.3f)), 0.45f, ground);
            Chunks(pos + Up * 0.1f, 12, 1f, 4.5f, Random.Range(4f, 6f), team, Hull, Licorice, 0.5f);
            if (!ground) return;
            var g = Ground(pos);
            Scorch(g, 1.4f);
            S.Smoulder(g + Up * 0.2f, 1f, WreckSmoke, 4f);
        }

        /// <summary>
        /// An aircraft is shot down: a burst in the air and pieces shed, then the airframe (`hulk`, may be null) falls
        /// trailing fire and smoke, tumbling, and explodes where it hits the ground. Fx takes ownership of `hulk`.
        /// </summary>
        public static void AircraftDestroyed(Transform hulk, Vector3 pos, Vector3 velocity, Color team)
        {
            Blast(pos, 0.55f, false);
            Chunks(pos, 7, 0.9f, 3.5f, 4.5f, team, Hull, Licorice, 0.5f, 0.4f);
            if (hulk != null)
                foreach (var mb in hulk.GetComponentsInChildren<MonoBehaviour>()) mb.enabled = false;
            S.Fall(hulk, pos, velocity + Up * 0.6f, team);
        }

        /// <summary>
        /// A building is destroyed: staggered blasts across its footprint, a collapse of dust, walls and roof thrown
        /// out as debris, smoke columns that linger, and a large scorch.
        /// </summary>
        public static void BuildingDestroyed(Vector3 center, int sizeX, Color team)
        {
            float n = Mathf.Max(1, sizeX), half = n * 0.5f, sn = Mathf.Sqrt(n);
            var c = Ground(center);
            Blast(c + Up * 0.4f * sn, 0.75f * sn, true);
            int bursts = 1 + sizeX * 2;
            for (int k = 0; k < bursts; k++)
                S.Later(Random.Range(0.1f, 0.35f + 0.35f * n),
                    c + new Vector3(Random.Range(-half, half) * 0.8f, Random.Range(0.2f, 0.6f) * sn, Random.Range(-half, half) * 0.8f),
                    Random.Range(0.5f, 0.9f) * (0.6f + 0.25f * n), true);
            int nd = (int)(10 + 10 * n);
            for (int i = 0; i < nd; i++)
            {
                var off = new Vector3(Random.Range(-half, half), 0f, Random.Range(-half, half));
                var outward = off.sqrMagnitude > 0.01f ? off.normalized : Flat(1f);
                Emit(S.Dust, c + off + Up * Random.Range(0.1f, 0.6f), outward * Random.Range(0.8f, 2.2f) + Up * Random.Range(0.2f, 0.9f),
                    Random.Range(0.7f, 1.2f) * (0.5f + 0.3f * n), Random.Range(3f, 5.5f), Vary(Color32.Lerp(DustPale, Cream, Random.value * 0.5f), 0.06f, 220));
            }
            Emit(S.Ring, c + Up * 0.05f, Vector3.zero, 3f * n + 2f, 0.9f, Alpha(DustPale, 0.7f), Random.Range(0f, 360f));
            Chunks(c + Up * 0.4f * sn, (int)(14 + 8 * n), 1.1f + 0.25f * n, 3.5f + n, Random.Range(6f, 9f), Cream, team, Hull, 0.35f);
            Chunks(c + Up * 0.2f, (int)(4 * n), 0.9f, 2.5f, 7f, Steel, Licorice, Cream, 0f);
            Scorch(c, n * 1.5f);
            int columns = 1 + sizeX / 2;
            for (int k = 0; k < columns; k++)
                S.Smoulder(c + new Vector3(Random.Range(-half, half) * 0.6f, 0.2f, Random.Range(-half, half) * 0.6f), 0.8f + 0.3f * n, Random.Range(8f, 10f), 6f);
        }

        /// <summary>An infantryman falls: no fireball, a puff of dust and a few kicked-up bits.</summary>
        public static void InfantryDeath(Vector3 pos, Color team)
        {
            var g = Ground(pos);
            for (int i = 0; i < 8; i++)
                Emit(S.Dust, g + Flat(0.12f) + Up * 0.12f, Flat(Random.Range(0.3f, 1f)) + Up * Random.Range(0.2f, 0.6f),
                    Random.Range(0.28f, 0.45f), Random.Range(1.2f, 2f), Vary(DustPale, 0.06f, 220));
            Chunks(g + Up * 0.15f, 6, 0.35f, 2.2f, 2.5f, Clod, team, DustBiscuit, 0f);
        }

        /// <summary>A mine goes off under a vehicle: a geyser of dirt and dust thrown straight up, a squat fireball.</summary>
        public static void Mine(Vector3 pos)
        {
            var g = Ground(pos);
            Blast(g + Up * 0.15f, 0.55f, true);
            Chunks(g + Up * 0.05f, 14, 0.8f, 6f, 3.5f, Clod, DustBiscuit, Licorice, 0f, 3f);
            for (int i = 0; i < 8; i++)
                Emit(S.Dust, g + Flat(0.15f) + Up * 0.2f, Up * Random.Range(1.5f, 3.5f) + Flat(0.4f), Random.Range(0.45f, 0.75f),
                    Random.Range(2f, 3.2f), Vary(DustBiscuit, 0.08f, 220));
            Scorch(g, 1.3f);
        }

        /// <summary>A structure is dismantled into salvage: cream sugar dust settling, a few panels falling off.</summary>
        public static void Salvaged(Vector3 pos, float size)
        {
            var g = Ground(pos);
            float sq = Mathf.Sqrt(size);
            int n = (int)(6 + 6 * size);
            for (int i = 0; i < n; i++)
                Emit(S.Dust, g + Flat(Random.Range(0f, 0.5f) * size) + Up * 0.2f, Flat(0.6f) + Up * Random.Range(0.3f, 0.9f),
                    Random.Range(0.5f, 0.9f) * sq, Random.Range(2f, 3.5f), Vary(Color32.Lerp(Cream, DustPale, 0.45f), 0.05f, 200));
            Chunks(g + Up * 0.2f, (int)(4 + 3 * size), 0.6f, 2.5f, 3f, Cream, DustPale, Steel, 0f);
            Emit(S.Ring, g + Up * 0.05f, Vector3.zero, 2f + 1.5f * size, 0.6f, Alpha(Cream, 0.45f), Random.Range(0f, 360f));
        }

        /// <summary>A damaged building smokes: one dark puff rising from its roof (Smoke system, lit, leaning with the wind).</summary>
        public static void DamageSmoke(Vector3 pos, float size) =>
            Emit(S.Smoke, pos, new Vector3(Random.Range(-0.12f, 0.12f), Random.Range(0.8f, 1.3f), Random.Range(-0.12f, 0.12f)),
                Random.Range(0.6f, 0.9f) * size, Random.Range(3f, 4.2f), SmokeColor(0.95f));

        static readonly Color32 FirePool = new Color32(255, 140, 50, 255);

        /// <summary>A burning building: a lick of flame, now and then an ember, and the fire's light flickering on the
        /// ground around it (a light pool, no per-pixel light: it reads in every view and stream).</summary>
        public static void DamageFire(Vector3 pos, float size, Vector3 ground)
        {
            Emit(S.Fire, pos + Random.insideUnitSphere * 0.08f, new Vector3(Random.Range(-0.1f, 0.1f), Random.Range(0.7f, 1.3f), Random.Range(-0.1f, 0.1f)),
                Random.Range(0.32f, 0.5f) * size, Random.Range(0.4f, 0.65f), White);
            if (Random.value < 0.25f)
                Emit(S.Sparks, pos, (Random.onUnitSphere * 0.6f + Up * 1.4f) * Random.Range(1f, 2f), 0.03f, Random.Range(0.5f, 0.9f), White);
            Emit(S.Pool, new Vector3(ground.x, 0.05f, ground.z), Vector3.zero, Random.Range(2.2f, 2.8f) * size, 0.3f, Alpha(FirePool, Random.Range(0.25f, 0.4f)));
        }

        /// <summary>A heavy gun's blast on the ground around the firer: a dust ring, and for artillery dust kicked outward.</summary>
        public static void GroundRing(Vector3 at, float size, float alpha, int puffs)
        {
            var g = Ground(at);
            Emit(S.Ring, g + Up * 0.05f, Vector3.zero, size, 0.35f + 0.1f * size, Alpha(Color32.Lerp(DustPale, Cream, 0.4f), alpha), Random.Range(0f, 360f));
            for (int i = 0; i < puffs; i++)
            {
                var dir = Flat(1f);
                Emit(S.Dust, g + dir * 0.4f + Up * 0.1f, dir * Random.Range(1f, 1.8f) + Up * Random.Range(0.1f, 0.4f),
                    Random.Range(0.35f, 0.55f), Random.Range(1.2f, 2f), Vary(DustBiscuit, 0.06f, 200));
            }
        }

        /// <summary>After an artillery shot: three gun-smoke puffs drift from the barrel tip and linger.</summary>
        public static void BarrelSmoke(Vector3 tip)
        {
            for (int i = 0; i < 3; i++)
                Emit(S.Dust, tip + Random.insideUnitSphere * 0.06f, Up * Random.Range(0.15f, 0.35f) + Random.insideUnitSphere * 0.1f,
                    Random.Range(0.22f, 0.32f), Random.Range(1.8f, 2.6f), Alpha(GunSmoke, 0.55f));
        }

        /// <summary>Artillery deploys: its spades bite, kicking dust back from a rear corner.</summary>
        public static void SpadeDust(Vector3 at, Vector3 back)
        {
            for (int i = 0; i < 2; i++)
                Emit(S.Dust, Ground(at) + Up * 0.08f, back * Random.Range(0.4f, 0.8f) + Flat(0.2f) + Up * Random.Range(0.15f, 0.35f),
                    Random.Range(0.25f, 0.38f), Random.Range(1f, 1.5f), Vary(DustBiscuit, 0.06f, 200));
        }

        /// <summary>A construction stage lands: a biscuit dust puff kicked out at each corner of the footprint.</summary>
        public static void StageDust(Vector3 center, float sx, float sz)
        {
            for (int i = 0; i < 4; i++)
            {
                var dir = new Vector3((i & 1) == 0 ? -1f : 1f, 0f, (i & 2) == 0 ? -1f : 1f);
                var at = new Vector3(center.x + dir.x * sx * 0.5f, 0.12f, center.z + dir.z * sz * 0.5f);
                for (int k = 0; k < 2; k++)
                    Emit(S.Dust, at + Random.insideUnitSphere * 0.1f, dir.normalized * Random.Range(0.6f, 1.2f) + Up * Random.Range(0.2f, 0.5f),
                        Random.Range(0.35f, 0.55f), Random.Range(1.2f, 1.9f), Vary(Color32.Lerp(DustBiscuit, DustPale, Random.value), 0.05f, 200));
            }
        }

        /// <summary>One ore brick poured from a truck's bin: tumbling down toward the bay, settling, fading. Crystal and
        /// uranium bricks glow (the shard shader's hot channel).</summary>
        public static void OreBrick(Vector3 lip, Vector3 dir, Color32 c, bool glow)
        {
            var v = dir * Random.Range(0.6f, 1.1f) + Up * Random.Range(0.8f, 1.4f) + Random.insideUnitSphere * 0.2f;
            S.EmitShard(lip + Random.insideUnitSphere * 0.06f, v, Random.Range(0.09f, 0.13f), Random.Range(2.2f, 3.2f), Vary(c, 0.1f, (byte)(glow ? 255 : 0)));
        }

        /// <summary>The ore lands in the bay: a puff of dust tinted toward the ore, rising over the truck so it reads from
        /// above (the stream's "a delivery is happening" burst).</summary>
        public static void PourDust(Vector3 pos, Color32 ore)
        {
            var c = Color32.Lerp(DustBiscuit, ore, 0.45f);
            for (int i = 0; i < 2; i++)
                Emit(S.Dust, pos + Random.insideUnitSphere * 0.1f, Flat(0.35f) + Up * Random.Range(0.6f, 1.0f),
                    Random.Range(0.5f, 0.7f), Random.Range(1.2f, 1.8f), Vary(c, 0.06f, 220));
        }

        static readonly Color32 SteamCol = new Color32(236, 228, 210, 175); // cream plastic, translucent

        /// <summary>A wisp of steam off a power plant tower: cream, cool (no glow), rising and spreading for about 2 s.</summary>
        public static void Steam(Vector3 pos, float size) =>
            Emit(S.Dust, pos + Random.insideUnitSphere * 0.05f, new Vector3(Random.Range(-0.1f, 0.1f), Random.Range(1.3f, 1.8f), Random.Range(-0.1f, 0.1f)),
                size * Random.Range(0.85f, 1.15f), Random.Range(1.7f, 2.3f), SteamCol);

        // ---------------------------------------------------------------- lines

        public static void Tracer(Vector3 a, Vector3 b, Color c)
        {
            var go = new GameObject("tracer");
            var lr = go.AddComponent<LineRenderer>();
            c = Hot(c, 1.4f);
            var mat = Mats.UnlitInstance(c, true);
            lr.sharedMaterial = mat;
            lr.positionCount = 2;
            lr.SetPosition(0, a); lr.SetPosition(1, b);
            lr.startWidth = 0.035f; lr.endWidth = 0.02f;
            lr.startColor = lr.endColor = Color.white;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var f = go.AddComponent<FxLife>();
            f.Life = 0.07f; f.Mat = mat; f.Color = c;
        }

        public static void Beam(Vector3 a, Vector3 b, Color c, float width, bool lamp = true)
        {
            var go = new GameObject("beam");
            var lr = go.AddComponent<LineRenderer>();
            var lampColor = c;
            c = Hot(c, 1.5f);
            var mat = Mats.UnlitInstance(c, true);
            lr.sharedMaterial = mat;
            lr.positionCount = 2;
            lr.SetPosition(0, a); lr.SetPosition(1, b);
            lr.startWidth = width; lr.endWidth = width * 0.6f;
            lr.startColor = lr.endColor = Color.white;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var f = go.AddComponent<FxLife>();
            f.Life = 0.22f; f.Mat = mat; f.Color = c;
            if (lamp) S.Lamp(b + Up * 0.3f, lampColor, 3f, 3f, 0.25f);
        }
    }
}
