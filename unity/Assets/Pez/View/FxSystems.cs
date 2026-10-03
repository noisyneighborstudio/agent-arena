using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Pez.View
{
    /// <summary>
    /// The particle engine behind <see cref="Fx"/>: one pooled, prewarmed world-space ParticleSystem per particle kind,
    /// fed by Emit with per-particle position, velocity, size, life and colour. The systems do the motion:
    /// buoyancy (negative gravity) and drag for fire and smoke, a light wind that leans plumes, gravity, drag and ground
    /// bounces for sparks, and a small rigid-body step for debris (tumble, bounce, slide, settle). Pooled point lights
    /// for flashes, delayed bursts, smouldering wrecks and falling aircraft hulks live in fixed arrays.
    /// Everything sits on the Default layer, so the main camera and every player-stream camera draw it.
    /// Nothing here allocates per frame.
    /// </summary>
    public class FxSystems : MonoBehaviour
    {
        static FxSystems inst;
        public static FxSystems I => inst != null ? inst : inst = Create();

        public ParticleSystem Flash, Fire, Smoke, Dust, Sparks, Shards, Ring, Scorch, Pool, Flame;
        Transform ground;
        /// <summary>
        /// The prevailing wind (world units/s, horizontal): one wind over the whole map, so every plume nearby leans the
        /// same way. It comes from the west-south-west at about half a tile a second, gusting slowly (plus or minus a
        /// quarter) and wandering a few degrees over minutes. Smoke, steam and dust drift with it at its speed.
        /// </summary>
        public static Vector3 Wind { get; private set; } = new Vector3(0.44f, 0f, 0.2f);
        const float WindSpeed = 0.5f, WindHeading = 24f; // heading in degrees from +x (east) toward +z (north)
        // Wind-borne systems and their drag: each gets force = wind x drag, so its particles drift at the wind's speed.
        readonly Dictionary<ParticleSystem, float> windBorne = new Dictionary<ParticleSystem, float>();

        static FxSystems Create()
        {
            var go = new GameObject("Fx");
            DontDestroyOnLoad(go);
            var fx = go.AddComponent<FxSystems>();
            fx.Build();
            return fx;
        }

        // ---------------------------------------------------------------- setup

        static Material Mat(string shader, int queue, params (string name, float value)[] props)
        {
            var s = Resources.Load<Shader>("PezShaders/" + shader);
            if (s == null) { Debug.LogWarning("Missing PezShaders/" + shader); s = Shader.Find("Pez/Unlit"); }
            var m = new Material(s) { renderQueue = queue };
            foreach (var (n, v) in props) m.SetFloat(n, v);
            return m;
        }

        static readonly List<ParticleSystemVertexStream> glowStreams = new List<ParticleSystemVertexStream>
            { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.StableRandomX };
        static readonly List<ParticleSystemVertexStream> smokeStreams = new List<ParticleSystemVertexStream>
            { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.UV, ParticleSystemVertexStream.AgePercent, ParticleSystemVertexStream.StableRandomX };
        static readonly List<ParticleSystemVertexStream> shardStreams = new List<ParticleSystemVertexStream>
            { ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color, ParticleSystemVertexStream.AgePercent };

        ParticleSystem Make(string name, Material mat, int max, float gravity, float drag, List<ParticleSystemVertexStream> streams,
            ParticleSystemRenderMode mode = ParticleSystemRenderMode.Billboard)
        {
            var go = new GameObject("fx_" + name);
            go.transform.SetParent(transform, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.loop = true;
            main.playOnAwake = false;
            main.duration = 5f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = max;
            main.gravityModifier = gravity;
            main.startSpeed = 0f;
            main.startLifetime = 1f;
            main.startSize = 1f;
            main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
            var em = ps.emission; em.enabled = false;
            var sh = ps.shape; sh.enabled = false;
            if (drag > 0f)
            {
                var lv = ps.limitVelocityOverLifetime;
                lv.enabled = true; lv.limit = 1000f; lv.dampen = 0f; lv.drag = drag;
                lv.multiplyDragByParticleSize = false; lv.multiplyDragByParticleVelocity = false;
            }
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.sharedMaterial = mat;
            r.renderMode = mode;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.Off;
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.alignment = ParticleSystemRenderSpace.View;
            r.applyActiveColorSpace = true;
            r.minParticleSize = 0f;
            r.maxParticleSize = 2f;
            r.sortMode = ParticleSystemSortMode.None;
            r.SetActiveVertexStreams(streams);
            ps.Play();
            return ps;
        }

        static Gradient Grad((float t, Color c)[] cols, (float t, float a)[] alphas)
        {
            var g = new Gradient();
            var ck = new GradientColorKey[cols.Length];
            for (int i = 0; i < cols.Length; i++) ck[i] = new GradientColorKey(cols[i].c, cols[i].t);
            var ak = new GradientAlphaKey[alphas.Length];
            for (int i = 0; i < alphas.Length; i++) ak[i] = new GradientAlphaKey(alphas[i].a, alphas[i].t);
            g.SetKeys(ck, ak);
            return g;
        }

        static void ColorOverLife(ParticleSystem ps, Gradient g) { var c = ps.colorOverLifetime; c.enabled = true; c.color = g; }
        static void SizeOverLife(ParticleSystem ps, params Keyframe[] keys) { var s = ps.sizeOverLifetime; s.enabled = true; s.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(keys)); }

        void Build()
        {
            ground = new GameObject("fx_ground").transform;
            ground.SetParent(transform, false);
            ground.position = Vector3.zero;

            // Flash: a white-hot core that collapses within a tenth of a second (additive).
            Flash = Make("flash", Mat("PezFxGlow", 3030, ("_Shape", 0), ("_Opacity", 0), ("_Boost", 1.5f)), 400, 0f, 0f, glowStreams);
            SizeOverLife(Flash, new Keyframe(0, 1f), new Keyframe(1, 0.35f));
            ColorOverLife(Flash, Grad(new[] { (0f, Color.white), (1f, new Color(1f, 0.75f, 0.4f)) }, new[] { (0f, 1f), (1f, 0f) }));

            // Fireball: hot gas pushed out by the blast, stopped by drag, lifted by buoyancy, cooling through
            // white, yellow, orange and red to dark soot. Partly occluding, so the dark end really darkens.
            Fire = Make("fire", Mat("PezFxGlow", 3010, ("_Shape", 3), ("_Opacity", 0.8f), ("_Boost", 0.92f), ("_Noise", 0.55f)), 4000, -0.32f, 4.5f, glowStreams);
            Fire.GetComponent<ParticleSystemRenderer>().sortMode = ParticleSystemSortMode.YoungestInFront;
            SizeOverLife(Fire, new Keyframe(0, 0.55f), new Keyframe(0.25f, 1f), new Keyframe(1, 1.3f));
            ColorOverLife(Fire, Grad(new[]
            {
                (0f, new Color(1f, 0.97f, 0.88f)), (0.14f, new Color(1f, 0.82f, 0.38f)), (0.34f, new Color(1f, 0.46f, 0.1f)),
                (0.6f, new Color(0.55f, 0.12f, 0.03f)), (1f, new Color(0.1f, 0.05f, 0.03f)),
            }, new[] { (0f, 1f), (0.55f, 0.85f), (1f, 0f) }));

            // Smoke: buoyant, slowed by drag, spreading as it rises, leaning with the wind, lit by the scene.
            // Young smoke glows from inside (the cooling fireball) for its first fifth.
            Smoke = Make("smoke", Mat("PezFxSmoke", 3000, ("_Heat", 1f), ("_HeatFade", 0.2f), ("_Noise", 0.65f)), 3500, -0.05f, 1.4f, smokeStreams);
            Smoke.GetComponent<ParticleSystemRenderer>().sortMode = ParticleSystemSortMode.Distance;
            SizeOverLife(Smoke, new Keyframe(0, 0.45f), new Keyframe(0.3f, 1f), new Keyframe(1, 1.75f));
            ColorOverLife(Smoke, Grad(new[] { (0f, Color.white), (1f, Color.white) }, new[] { (0f, 0f), (0.05f, 0.95f), (0.5f, 0.7f), (1f, 0f) }));
            Turbulence(Smoke, 0.15f, 1.4f);

            // Flame: a burning building's tongues of fire. Small, short-lived, buoyant glows stretched along their rise
            // so they read as licks of flame, tapering and reddening as they climb, flickering in a fine noise and leaning
            // with the wind (not the fireball's expanding blob, which reads as a shell landing).
            Flame = Make("flame", Mat("PezFxGlow", 3015, ("_Shape", 0), ("_Opacity", 0.6f), ("_Boost", 1.45f), ("_Noise", 0.5f)), 4000, -0.9f, 1.6f, glowStreams,
                ParticleSystemRenderMode.Stretch);
            var fr = Flame.GetComponent<ParticleSystemRenderer>();
            fr.velocityScale = 0.22f; fr.lengthScale = 1.35f; fr.sortMode = ParticleSystemSortMode.YoungestInFront;
            SizeOverLife(Flame, new Keyframe(0, 0.7f), new Keyframe(0.2f, 1f), new Keyframe(1, 0f));
            ColorOverLife(Flame, Grad(new[]
            {
                (0f, new Color(1f, 0.86f, 0.45f)), (0.25f, new Color(1f, 0.55f, 0.12f)), (0.6f, new Color(0.88f, 0.24f, 0.05f)), (1f, new Color(0.35f, 0.08f, 0.03f)),
            }, new[] { (0f, 0f), (0.08f, 1f), (0.6f, 0.8f), (1f, 0f) }));
            Turbulence(Flame, 0.3f, 1.6f);
            var fn = Flame.noise; fn.frequency = 2.2f; fn.scrollSpeed = 1.2f; // fine, fast flicker

            // Dust: cool, heavier than smoke, kicked out along the ground and settling (no glow).
            Dust = Make("dust", Mat("PezFxSmoke", 3000, ("_Heat", 0f), ("_Noise", 0.7f)), 3000, -0.01f, 2.4f, smokeStreams);
            Dust.GetComponent<ParticleSystemRenderer>().sortMode = ParticleSystemSortMode.Distance;
            SizeOverLife(Dust, new Keyframe(0, 0.5f), new Keyframe(0.25f, 1f), new Keyframe(1, 1.9f));
            ColorOverLife(Dust, Grad(new[] { (0f, Color.white), (1f, Color.white) }, new[] { (0f, 0f), (0.04f, 0.9f), (0.4f, 0.6f), (1f, 0f) }));
            Turbulence(Dust, 0.1f, 2.4f);

            // Sparks and embers: ballistic under gravity with a little drag, stretched along their motion, bouncing off
            // the ground and cooling from yellow-white to red.
            Sparks = Make("sparks", Mat("PezFxGlow", 3020, ("_Shape", 0), ("_Opacity", 0), ("_Boost", 2.5f)), 5000, 1f, 0.7f, glowStreams,
                ParticleSystemRenderMode.Stretch);
            var sr = Sparks.GetComponent<ParticleSystemRenderer>();
            sr.velocityScale = 0.035f; sr.lengthScale = 1.6f;
            ColorOverLife(Sparks, Grad(new[] { (0f, new Color(1f, 0.95f, 0.75f)), (0.35f, new Color(1f, 0.62f, 0.18f)), (1f, new Color(0.75f, 0.16f, 0.04f)) },
                new[] { (0f, 1f), (0.7f, 0.85f), (1f, 0f) }));
            var col = Sparks.collision;
            col.enabled = true; col.type = ParticleSystemCollisionType.Planes; col.SetPlane(0, ground);
            col.bounce = 0.38f; col.dampen = 0.35f; col.lifetimeLoss = 0f; col.radiusScale = 0.3f;
            col.sendCollisionMessages = false;

            // Debris: faceted shards, simulated in StepShards (gravity, drag, bounce, friction, settle).
            Shards = Make("shards", Mat("PezFxShard", 2000), MaxShards, 0f, 0f, shardStreams, ParticleSystemRenderMode.Mesh);
            var shr = Shards.GetComponent<ParticleSystemRenderer>();
            shr.SetMeshes(ShardMeshes());
            shr.alignment = ParticleSystemRenderSpace.World;
            shr.shadowCastingMode = ShadowCastingMode.On;
            var sm = Shards.main; sm.startRotation3D = true;
            SizeOverLife(Shards, new Keyframe(0, 1f), new Keyframe(0.86f, 1f), new Keyframe(1, 0f));

            // Shockwave: a dust ring racing out along the ground, decelerating (alpha-blended, flat on the ground).
            Ring = Make("ring", Mat("PezFxGlow", 2995, ("_Shape", 1), ("_Opacity", 1f), ("_Boost", 1f), ("_Noise", 0.6f)), 300, 0f, 0f, glowStreams,
                ParticleSystemRenderMode.HorizontalBillboard);
            SizeOverLife(Ring, new Keyframe(0, 0.12f, 0f, 4f), new Keyframe(0.35f, 0.78f), new Keyframe(1, 1f));
            ColorOverLife(Ring, Grad(new[] { (0f, Color.white), (1f, Color.white) }, new[] { (0f, 1f), (0.5f, 0.55f), (1f, 0f) }));

            // Scorch: a dark blotch burnt into the ground that fades over a minute or so (Fx.Scorch sets the life).
            Scorch = Make("scorch", Mat("PezFxGlow", 2991, ("_Shape", 2), ("_Opacity", 1f), ("_Boost", 1f), ("_Noise", 0.6f)), 900, 0f, 0f, glowStreams,
                ParticleSystemRenderMode.HorizontalBillboard);
            SizeOverLife(Scorch, new Keyframe(0, 0.5f), new Keyframe(0.01f, 1f), new Keyframe(1, 1f));
            ColorOverLife(Scorch, Grad(new[] { (0f, Color.white), (1f, Color.white) }, new[] { (0f, 1f), (0.55f, 0.9f), (1f, 0f) }));

            // Light pool: the warm splash an explosion or muzzle flash throws on the ground around it (additive, flat on
            // the ground under the fog overlay). Every lamp makes one, so the light reads in every view and on every
            // stream even when the per-pixel point-light budget is spent.
            Pool = Make("pool", Mat("PezFxGlow", 2993, ("_Shape", 0), ("_Opacity", 0f), ("_Boost", 0.9f)), 300, 0f, 0f, glowStreams,
                ParticleSystemRenderMode.HorizontalBillboard);
            SizeOverLife(Pool, new Keyframe(0, 0.8f), new Keyframe(0.2f, 1f), new Keyframe(1, 1.05f));
            ColorOverLife(Pool, Grad(new[] { (0f, Color.white), (1f, new Color(1f, 0.6f, 0.35f)) }, new[] { (0f, 1f), (0.3f, 0.55f), (1f, 0f) }));

            for (int i = 0; i < LightCount; i++)
            {
                var lg = new GameObject("fx_light");
                lg.transform.SetParent(transform, false);
                var l = lg.AddComponent<Light>();
                l.type = LightType.Point; l.shadows = LightShadows.None; l.renderMode = LightRenderMode.Auto;
                l.enabled = false;
                lights[i] = l;
            }
        }

        /// <summary>A little billow (noise) and the prevailing wind; `drag` is the system's drag, so it drifts at wind speed.</summary>
        void Turbulence(ParticleSystem ps, float strength, float drag)
        {
            var n = ps.noise;
            n.enabled = true; n.strength = strength; n.frequency = 0.6f; n.scrollSpeed = 0.15f;
            n.quality = ParticleSystemNoiseQuality.Low; n.octaveCount = 1; n.damping = true;
            var f = ps.forceOverLifetime;
            f.enabled = true; f.space = ParticleSystemSimulationSpace.World;
            windBorne[ps] = drag;
            ApplyWind(ps, drag);
        }

        static void ApplyWind(ParticleSystem ps, float drag)
        {
            var f = ps.forceOverLifetime;
            f.x = Wind.x * drag; f.y = 0f; f.z = Wind.z * drag;
        }

        /// <summary>The wind now: slow gusts and a gently wandering heading (smooth, so plumes bend rather than snap).</summary>
        static Vector3 WindAt(float t)
        {
            float gust = 1f + 0.18f * Mathf.Sin(t * 0.21f) + 0.08f * Mathf.Sin(t * 0.53f + 1.3f);
            float heading = (WindHeading + 9f * Mathf.Sin(t * 0.017f) + 4f * Mathf.Sin(t * 0.061f + 0.7f)) * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(heading), 0f, Mathf.Sin(heading)) * WindSpeed * gust;
        }

        /// <summary>Faceted chips: jittered, flattened octahedra and a wedge, flat-shaded (split vertices per face).</summary>
        static Mesh[] ShardMeshes()
        {
            var rnd = new System.Random(7);
            float R(float a, float b) => a + (float)rnd.NextDouble() * (b - a);
            var meshes = new Mesh[4];
            int[] octa = { 2, 4, 0, 2, 0, 5, 2, 5, 1, 2, 1, 4, 3, 0, 4, 3, 5, 0, 3, 1, 5, 3, 4, 1 };
            for (int m = 0; m < 3; m++)
            {
                var p = new[]
                {
                    new Vector3(R(0.7f, 1f), R(-0.1f, 0.1f), R(-0.2f, 0.2f)), new Vector3(-R(0.5f, 1f), R(-0.1f, 0.1f), R(-0.2f, 0.2f)),
                    new Vector3(R(-0.2f, 0.2f), R(0.35f, 0.55f), R(-0.2f, 0.2f)), new Vector3(R(-0.2f, 0.2f), -R(0.3f, 0.5f), R(-0.2f, 0.2f)),
                    new Vector3(R(-0.2f, 0.2f), R(-0.1f, 0.1f), R(0.5f, 0.9f)), new Vector3(R(-0.2f, 0.2f), R(-0.1f, 0.1f), -R(0.5f, 0.9f)),
                };
                meshes[m] = Faceted(p, octa);
            }
            // Wedge: a broken panel corner.
            var w = new[]
            {
                new Vector3(-1f, -0.25f, -0.6f), new Vector3(1f, -0.25f, -0.7f), new Vector3(-0.2f, -0.25f, 0.9f),
                new Vector3(-1f, 0.25f, -0.6f), new Vector3(0.9f, 0.2f, -0.7f), new Vector3(-0.2f, 0.3f, 0.8f),
            };
            meshes[3] = Faceted(w, new[] { 0, 1, 2, 3, 5, 4, 0, 3, 4, 0, 4, 1, 1, 4, 5, 1, 5, 2, 2, 5, 3, 2, 3, 0 });
            return meshes;
        }

        static Mesh Faceted(Vector3[] p, int[] tris)
        {
            var c = Vector3.zero; foreach (var v in p) c += v; c /= p.Length;
            var verts = new Vector3[tris.Length]; var norms = new Vector3[tris.Length]; var idx = new int[tris.Length];
            for (int i = 0; i < tris.Length; i += 3)
            {
                Vector3 a = p[tris[i]], b = p[tris[i + 1]], d = p[tris[i + 2]];
                var n = Vector3.Cross(b - a, d - a).normalized;
                if (Vector3.Dot(n, (a + b + d) / 3f - c) < 0) { (b, d) = (d, b); n = -n; }
                verts[i] = a; verts[i + 1] = b; verts[i + 2] = d;
                norms[i] = norms[i + 1] = norms[i + 2] = n;
                idx[i] = i; idx[i + 1] = i + 1; idx[i + 2] = i + 2;
            }
            var mesh = new Mesh { name = "fx_shard", vertices = verts, normals = norms, triangles = idx };
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Emit one invisible particle per system so shaders and buffers are ready before the first battle.</summary>
        public void Prewarm(Vector3 at)
        {
            var clear = new Color32(0, 0, 0, 0);
            Emit(Flash, at, Vector3.zero, 0.1f, 0.2f, clear);
            Emit(Fire, at, Vector3.zero, 0.1f, 0.2f, clear);
            Emit(Smoke, at, Vector3.zero, 0.1f, 0.2f, clear);
            Emit(Dust, at, Vector3.zero, 0.1f, 0.2f, clear);
            Emit(Sparks, at, Vector3.up, 0.01f, 0.2f, clear);
            Emit(Ring, at, Vector3.zero, 0.1f, 0.2f, clear);
            Emit(Scorch, at, Vector3.zero, 0.1f, 0.2f, clear);
            Emit(Pool, at, Vector3.zero, 0.1f, 0.2f, clear);
            Emit(Flame, at, Vector3.up, 0.1f, 0.2f, clear);
            EmitShard(at + Vector3.down * 3f, Vector3.zero, 0.01f, 0.2f, clear);
        }

        // ---------------------------------------------------------------- audiences

        readonly Dictionary<(ParticleSystem, int), ParticleSystem> audiences = new Dictionary<(ParticleSystem, int), ParticleSystem>();

        /// <summary>
        /// A copy of a system drawn on one render layer only, so an effect can be shown to some cameras and not others
        /// (the main view's layer, or one player stream's team layer: see TerrainView's fog layers). Made once, on demand.
        /// </summary>
        public ParticleSystem OnLayer(ParticleSystem src, int layer)
        {
            if (audiences.TryGetValue((src, layer), out var ps) && ps != null) return ps;
            var go = Instantiate(src.gameObject, src.transform.parent);
            go.name = $"{src.name}@{layer}";
            foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
            ps = go.GetComponent<ParticleSystem>();
            ps.Clear(true);
            ps.Play(true);
            if (windBorne.TryGetValue(src, out var drag)) { windBorne[ps] = drag; ApplyWind(ps, drag); }
            audiences[(src, layer)] = ps;
            return ps;
        }

        // ---------------------------------------------------------------- emission

        public static void Emit(ParticleSystem ps, Vector3 pos, Vector3 vel, float size, float life, Color32 color, float rotation = 0f)
        {
            var e = new ParticleSystem.EmitParams
            {
                position = pos, velocity = vel, startSize = size, startLifetime = life, startColor = color, rotation = rotation,
            };
            ps.Emit(e, 1);
        }

        public void EmitShard(Vector3 pos, Vector3 vel, float size, float life, Color32 color)
        {
            var e = new ParticleSystem.EmitParams
            {
                position = pos, velocity = vel, startSize = size, startLifetime = life, startColor = color,
                rotation3D = new Vector3(Random.Range(0f, 360f), Random.Range(0f, 360f), Random.Range(0f, 360f)),
                angularVelocity3D = Random.onUnitSphere * Random.Range(180f, 720f),
                meshIndex = Random.Range(0, 4),
            };
            Shards.Emit(e, 1);
        }

        // ---------------------------------------------------------------- lights

        const int LightCount = 12;
        readonly Light[] lights = new Light[LightCount];
        readonly float[] lightStart = new float[LightCount], lightLife = new float[LightCount], lightPeak = new float[LightCount];
        int lightNext;

        public void Lamp(Vector3 pos, Color c, float intensity, float range, float life)
        {
            int i = -1;
            for (int k = 0; k < LightCount; k++) { int j = (lightNext + k) % LightCount; if (!lights[j].enabled) { i = j; break; } }
            if (i < 0) i = lightNext; // all busy: take the oldest
            lightNext = (i + 1) % LightCount;
            var l = lights[i];
            l.transform.position = pos;
            l.color = c; l.range = range; l.intensity = intensity; l.enabled = true;
            lightStart[i] = Time.time; lightLife[i] = life; lightPeak[i] = intensity;
            // The ground splash under it, weaker the higher the light is (an air burst lights the ground less).
            float a = Mathf.Clamp01(intensity / 4.5f) * Mathf.Clamp01(1f - pos.y / (range * 1.1f));
            if (a > 0.03f)
                Emit(Pool, new Vector3(pos.x, 0.05f, pos.z), Vector3.zero, range * 1.25f, Mathf.Max(0.12f, life * 1.8f),
                    new Color32((byte)(Mathf.Clamp01(c.r) * 255), (byte)(Mathf.Clamp01(c.g) * 255), (byte)(Mathf.Clamp01(c.b) * 255), (byte)(a * 255)));
        }

        // ---------------------------------------------------------------- delayed bursts, smoulder, falling hulks

        public struct Pending { public float At; public Vector3 Pos; public float Size; public bool Ground; }
        readonly Pending[] pending = new Pending[256];
        int pendingCount;
        public void Later(float delay, Vector3 pos, float size, bool ground)
        {
            if (pendingCount >= pending.Length) return;
            pending[pendingCount++] = new Pending { At = Time.time + delay, Pos = pos, Size = size, Ground = ground };
        }

        struct Burner { public Vector3 Pos; public float Start, End, Rate, Acc, Size; }
        readonly Burner[] smoulders = new Burner[160];
        int smoulderCount;
        /// <summary>A wreck that keeps burning: a column of smoke (and early flames) for `seconds`.</summary>
        public void Smoulder(Vector3 pos, float size, float seconds, float rate)
        {
            if (smoulderCount >= smoulders.Length) return;
            smoulders[smoulderCount++] = new Burner { Pos = pos, Start = Time.time, End = Time.time + seconds, Rate = rate, Size = size };
        }

        struct Faller { public Transform Hulk; public Vector3 Pos, Vel, Spin; public float Acc, Born; public Color32 Team; }
        readonly Faller[] fallers = new Faller[48];
        int fallerCount;
        public void Fall(Transform hulk, Vector3 pos, Vector3 vel, Color32 team)
        {
            if (fallerCount >= fallers.Length) { Fx.VehicleDestroyed(new Vector3(pos.x, 0.2f, pos.z), team); if (hulk) Destroy(hulk.gameObject); return; }
            fallers[fallerCount++] = new Faller
            {
                Hulk = hulk, Pos = pos, Vel = vel, Born = Time.time, Team = team,
                Spin = new Vector3(Random.Range(-60f, 60f), Random.Range(-160f, 160f), Random.Range(140f, 320f) * (Random.value < 0.5f ? -1 : 1)),
            };
        }

        // ---------------------------------------------------------------- per frame

        const int MaxShards = 1200;
        readonly ParticleSystem.Particle[] shardBuf = new ParticleSystem.Particle[MaxShards];

        void Update()
        {
            float dt = Time.deltaTime, now = Time.time;
            if (dt <= 0f) return;

            // Every wind-borne system (and its per-audience copies) follows the one prevailing wind.
            if (Time.frameCount % 10 == 0)
            {
                Wind = WindAt(now);
                foreach (var kv in windBorne) if (kv.Key != null) ApplyWind(kv.Key, kv.Value);
            }

            for (int i = 0; i < LightCount; i++)
            {
                var l = lights[i];
                if (!l.enabled) continue;
                float t = (now - lightStart[i]) / lightLife[i];
                if (t >= 1f) { l.enabled = false; continue; }
                float k = 1f - t;
                l.intensity = lightPeak[i] * k * k;
            }

            for (int i = pendingCount - 1; i >= 0; i--)
            {
                if (now < pending[i].At) continue;
                var p = pending[i];
                pending[i] = pending[--pendingCount];
                Fx.Blast(p.Pos, p.Size, p.Ground);
            }

            for (int i = smoulderCount - 1; i >= 0; i--)
            {
                ref var s = ref smoulders[i];
                if (now >= s.End) { smoulders[i] = smoulders[--smoulderCount]; continue; }
                float t = (now - s.Start) / (s.End - s.Start);
                s.Acc += s.Rate * (1f - 0.75f * t) * dt;
                while (s.Acc >= 1f)
                {
                    s.Acc -= 1f;
                    float sq = Mathf.Sqrt(s.Size);
                    var at = s.Pos + new Vector3(Random.Range(-0.25f, 0.25f), 0f, Random.Range(-0.25f, 0.25f)) * s.Size;
                    Emit(Smoke, at + Vector3.up * 0.15f, new Vector3(Random.Range(-0.15f, 0.15f), Random.Range(0.7f, 1.3f) * sq, Random.Range(-0.15f, 0.15f)),
                        Random.Range(0.35f, 0.6f) * s.Size, Random.Range(2.6f, 4.2f), Fx.SmokeColor(0.9f));
                    if (t < 0.45f && Random.value < 0.6f)
                        Emit(Fire, at + Vector3.up * 0.1f, new Vector3(0f, Random.Range(0.6f, 1.4f), 0f), Random.Range(0.18f, 0.32f) * s.Size, Random.Range(0.3f, 0.55f), Fx.White);
                }
            }

            for (int i = fallerCount - 1; i >= 0; i--)
            {
                ref var f = ref fallers[i];
                f.Vel.y -= 9.81f * 0.8f * dt;
                f.Vel.x -= f.Vel.x * 0.25f * dt; f.Vel.z -= f.Vel.z * 0.25f * dt;
                f.Pos += f.Vel * dt;
                if (f.Hulk != null) { f.Hulk.position = f.Pos; f.Hulk.Rotate(f.Spin * dt, Space.Self); }
                f.Acc += 50f * dt;
                while (f.Acc >= 1f)
                {
                    f.Acc -= 1f;
                    Emit(Fire, f.Pos + Random.insideUnitSphere * 0.12f, -f.Vel * 0.1f + Random.insideUnitSphere * 0.4f, Random.Range(0.25f, 0.4f), Random.Range(0.25f, 0.45f), Fx.White);
                    if (Random.value < 0.6f)
                        Emit(Smoke, f.Pos, -f.Vel * 0.05f + Vector3.up * 0.2f, Random.Range(0.3f, 0.5f), Random.Range(1.8f, 3f), Fx.SmokeColor(0.8f));
                }
                if (f.Pos.y <= 0.12f || now - f.Born > 6f)
                {
                    Fx.VehicleDestroyed(new Vector3(f.Pos.x, 0.2f, f.Pos.z), f.Team);
                    if (f.Hulk != null) Destroy(f.Hulk.gameObject);
                    fallers[i] = fallers[--fallerCount];
                }
            }

            StepShards(dt);
        }

        /// <summary>
        /// Debris as little rigid bodies: gravity and air drag, a lossy bounce off the ground that bleeds spin, then
        /// sliding friction until the shard settles and lies still. The particle system integrates position and rotation.
        /// </summary>
        void StepShards(float dt)
        {
            int n = Shards.particleCount;
            if (n == 0) return;
            n = Shards.GetParticles(shardBuf);
            float drag = 1f - 0.35f * dt, friction = Mathf.Max(0f, 1f - 7f * dt), spinStop = Mathf.Max(0f, 1f - 9f * dt);
            for (int i = 0; i < n; i++)
            {
                ref var p = ref shardBuf[i];
                var v = p.velocity;
                var pos = p.position;
                float half = p.startSize * 0.4f;
                v.y -= 9.81f * dt;
                v *= drag;
                if (pos.y <= half && v.y <= 0f)
                {
                    pos.y = half;
                    if (v.y < -1.0f)
                    {
                        v.y = -v.y * 0.32f;
                        v.x *= 0.6f; v.z *= 0.6f;
                        p.angularVelocity3D *= 0.55f;
                    }
                    else
                    {
                        v.y = 0f;
                        v.x *= friction; v.z *= friction;
                        p.angularVelocity3D *= spinStop;
                    }
                    p.position = pos;
                }
                p.velocity = v;
            }
            Shards.SetParticles(shardBuf, n);
        }
    }
}
