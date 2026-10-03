using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// The scene's look outside post-processing: sun, sky fill, the sky the shiny materials reflect, shadow settings for
    /// the orthographic camera, and the PBR response of the art pack's materials (by material name).
    /// See docs/render/QUALITY_PASS.md.
    /// </summary>
    public static class Look
    {
        // ---------------------------------------------------------------- lighting

        // Art pack render.html: warm sun #FFE6C4 from the upper left of the screen, shadows to the lower right, and a
        // cool sky fill so shaded faces go cool, not grey. The filmic tonemap (PezPost) rolls the highlights off.
        public static Color SunColor = new Color32(255, 228, 192, 255);
        public static float SunIntensity = 1.35f, ShadowStrength = 0.82f, ShadowBias = 0.02f, ShadowNormalBias = 0.3f;
        public static Color AmbientSky = new Color(0.50f, 0.58f, 0.74f);   // cool sky from above
        public static Color AmbientEquator = new Color(0.47f, 0.46f, 0.47f);
        public static Color AmbientGround = new Color32(98, 80, 58, 255);  // warm bounce from the biscuit ground
        public static float ReflectionIntensity = 0.85f;
        /// <summary>The reflected sky (Look.SkyCube): kept near neutral so glossy team paint and metal don't go blue.</summary>
        public static Color SkyZenith = new Color(0.60f, 0.63f, 0.68f), SkyUpper = new Color(0.74f, 0.75f, 0.77f),
                            SkyHorizon = new Color(0.90f, 0.87f, 0.81f), SkyGround = new Color(0.34f, 0.29f, 0.22f);
        /// <summary>Global scales on Pez/Model's bevel highlight and grime (per-material values in the table below).</summary>
        public static float EdgeScale = 1.5f, GrimeScale = 1f;
        static Light sunLight;

        /// <summary>Sun, ambient, reflections and shadow quality. Called once from GameRunner.SetupScene.</summary>
        public static void SetupLighting(Light sun)
        {
            sunLight = sun;
            sun.shadows = LightShadows.Soft;
            sun.shadowNearPlane = 0.2f;
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            // Without a skybox the default reflection is black, so metal and gloss reflected nothing and read flat. Give
            // them a soft studio sky: cool zenith, bright warm horizon haze, biscuit ground.
            RenderSettings.defaultReflectionMode = UnityEngine.Rendering.DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = SkyCube();
            // One cascade, fitted to the view each frame (RtsCamera.ShadowReach): the orthographic camera sees a thin
            // slab of depth, which cascades split by distance would waste. Very high resolution, soft (PCF) filtering.
            QualitySettings.shadowCascades = 1;
            QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
            QualitySettings.shadowProjection = ShadowProjection.CloseFit;
            QualitySettings.shadows = ShadowQuality.All;
            QualitySettings.pixelLightCount = 4; // explosion and muzzle lights (FxSystems.Lamp)
            ApplyLighting();
            LookTune.Start();
        }

        /// <summary>(Re)apply the tunable lighting values above.</summary>
        public static void ApplyLighting()
        {
            if (sunLight != null)
            {
                sunLight.color = SunColor;
                sunLight.intensity = SunIntensity;
                sunLight.shadowStrength = ShadowStrength;
                // The shadow map covers just the view (RtsCamera.ShadowReach), so texels are small: little bias needed.
                sunLight.shadowBias = ShadowBias;
                sunLight.shadowNormalBias = ShadowNormalBias;
            }
            RenderSettings.ambientSkyColor = AmbientSky;
            RenderSettings.ambientEquatorColor = AmbientEquator;
            RenderSettings.ambientGroundColor = AmbientGround;
            RenderSettings.reflectionIntensity = ReflectionIntensity;
            var key = (SkyZenith, SkyUpper, SkyHorizon, SkyGround);
            if (sky != null && !key.Equals(skyKey)) { FillSky(sky); }
            Shader.SetGlobalFloat("_PezEdgeScale", EdgeScale);
            Shader.SetGlobalFloat("_PezGrimeScale", GrimeScale);
        }

        static Cubemap sky;
        static (Color, Color, Color, Color) skyKey;

        /// <summary>A small procedural sky cubemap (with mips, for rough reflections).</summary>
        public static Cubemap SkyCube()
        {
            if (sky != null) return sky;
            sky = new Cubemap(64, TextureFormat.RGBA32, true) { name = "PezSky", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
            FillSky(sky);
            return sky;
        }

        static void FillSky(Cubemap cube)
        {
            skyKey = (SkyZenith, SkyUpper, SkyHorizon, SkyGround);
            int N = cube.width;
            var deep = SkyGround * 0.6f;
            var px = new Color[N * N];
            for (int f = 0; f < 6; f++)
            {
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        // D3D cube face layout (Unity's cubemap rows run top to bottom): u right, v down.
                        float u = 2f * (x + 0.5f) / N - 1f, v = 2f * (y + 0.5f) / N - 1f;
                        Vector3 d = f switch
                        {
                            0 => new Vector3(1, -v, -u),
                            1 => new Vector3(-1, -v, u),
                            2 => new Vector3(u, 1, v),
                            3 => new Vector3(u, -1, -v),
                            4 => new Vector3(u, -v, 1),
                            _ => new Vector3(-u, -v, -1),
                        };
                        float h = d.normalized.y;
                        Color c = h >= 0
                            ? (h < 0.25f ? Color.Lerp(SkyHorizon, SkyUpper, h / 0.25f) : Color.Lerp(SkyUpper, SkyZenith, Mathf.Pow((h - 0.25f) / 0.75f, 0.8f)))
                            : (h > -0.15f ? Color.Lerp(SkyHorizon, SkyGround, -h / 0.15f) : Color.Lerp(SkyGround, deep, (-h - 0.15f) / 0.85f));
                        c.a = 1f;
                        px[y * N + x] = c;
                    }
                cube.SetPixels(px, (CubemapFace)f);
            }
            cube.Apply(true, false);
        }

        // ---------------------------------------------------------------- materials

        /// <summary>Per material name: smoothness, metallic, emission boost (HDR, feeds the bloom), bevel highlight.</summary>
        struct Pbr
        {
            public float Smooth, Metal, Glow, Edge, Spec;
            public Pbr(float s, float m, float g, float e, float spec = 1f) { Smooth = s; Metal = m; Glow = g; Edge = e; Spec = spec; }
        }

        static readonly (string prefix, Pbr pbr)[] table =
        {
            ("M_Team",         new Pbr(0.55f, 0.00f, 0f, 0.30f, 0.3f)), // candy paint with a third of the usual reflectance: stays vivid
            ("M_CreamPlastic", new Pbr(0.45f, 0.00f, 0f, 0.28f)),
            ("M_SmokePlastic", new Pbr(0.40f, 0.00f, 0f, 0.55f, 0.7f)),
            ("M_SpringSteel",  new Pbr(0.58f, 0.85f, 0f, 0.45f)), // gunmetal
            ("M_Foil",         new Pbr(0.55f, 0.75f, 0f, 0.30f)),
            ("M_Licorice",     new Pbr(0.46f, 0.00f, 0f, 0.65f)),
            ("M_Dark",         new Pbr(0.42f, 0.10f, 0f, 0.55f)),
            ("M_SugarPad",     new Pbr(0.10f, 0.00f, 0f, 0.22f)), // concrete pads
            ("M_Kraft",        new Pbr(0.18f, 0.00f, 0f, 0.25f)),
            ("M_Bone",         new Pbr(0.42f, 0.00f, 0f, 0.25f)),
            ("M_GrapeVent",    new Pbr(0.35f, 0.00f, 0f, 0.45f)),
            ("M_E_",           new Pbr(0.60f, 0.00f, 2.6f, 0.00f)), // emissives glow into the bloom
            ("M_OreTint",      new Pbr(0.55f, 0.30f, 0f, 0.35f)),
            ("M_Ore_Copper",   new Pbr(0.58f, 0.55f, 0f, 0.35f)),
            ("M_Ore_",         new Pbr(0.40f, 0.00f, 0f, 0.35f)),
            ("M_Cliff_Licorice", new Pbr(0.32f, 0.00f, 0f, 0.40f)),
            ("M_Cliff_",       new Pbr(0.12f, 0.00f, 0f, 0.30f)),
        };

        static Shader modelShader;
        static Shader ModelShader => modelShader != null ? modelShader : modelShader = Resources.Load<Shader>("PezShaders/PezModel");
        static readonly Dictionary<Material, Material> converted = new Dictionary<Material, Material>();
        static readonly int BaseColorId = Shader.PropertyToID("baseColorFactor"), EmissiveId = Shader.PropertyToID("emissiveFactor"),
            RoughId = Shader.PropertyToID("roughnessFactor"), MetalId = Shader.PropertyToID("metallicFactor"), CullId = Shader.PropertyToID("_CullMode");

        /// <summary>Swap a glTF instance's opaque materials for tuned Pez/Model ones (shared per source material; names kept,
        /// so TintTeam / TintOre still find M_Team and M_OreTint).</summary>
        public static void Convert(GameObject go)
        {
            if (ModelShader == null) return;
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                var mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    if (!converted.TryGetValue(m, out var c)) converted[m] = c = Make(m);
                    if (c != m) { mats[i] = c; changed = true; }
                }
                if (changed) r.sharedMaterials = mats;
            }
        }

        static Material Make(Material src)
        {
            if (src.shader == null || !src.shader.name.StartsWith("glTF/PbrMetallicRoughness")) return src;
            var baseColor = src.HasProperty(BaseColorId) ? src.GetColor(BaseColorId) : src.color;
            // Glass and anything blended keeps glTFast's shader (it handles transparency); just give glass some gloss.
            if (baseColor.a < 0.99f || src.IsKeywordEnabled("_ALPHABLEND_ON") || src.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))
            {
                if (src.HasProperty(RoughId)) src.SetFloat(RoughId, Mathf.Min(src.GetFloat(RoughId), 0.12f));
                return src;
            }
            var pbr = Lookup(src.name, out bool known);
            var m = new Material(ModelShader) { name = src.name, enableInstancing = true, color = baseColor };
            float rough = src.HasProperty(RoughId) ? src.GetFloat(RoughId) : 0.5f;
            m.SetFloat("_Glossiness", known ? pbr.Smooth : 1f - rough);
            m.SetFloat("_Metallic", known ? pbr.Metal : (src.HasProperty(MetalId) ? src.GetFloat(MetalId) : 0f));
            var em = src.HasProperty(EmissiveId) ? src.GetColor(EmissiveId) : Color.black;
            m.SetColor("_EmissionColor", em * (pbr.Glow > 0 ? pbr.Glow : 1f));
            m.SetFloat("_Edge", known ? pbr.Edge : 0.3f);
            m.SetFloat("_SpecK", known ? pbr.Spec : 1f);
            if (src.HasProperty(CullId)) m.SetFloat("_CullMode", src.GetFloat(CullId));
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            return m;
        }

        static Pbr Lookup(string name, out bool known)
        {
            foreach (var (prefix, pbr) in table)
                if (name.StartsWith(prefix)) { known = true; return pbr; }
            known = false;
            return new Pbr(0.4f, 0f, 0f, 0.3f);
        }

        /// <summary>A Pez/Model material for procedural geometry (the cliff massifs).</summary>
        public static Material Model(Color c, float smooth, float metal, float edge, float grime = 0.5f)
        {
            if (ModelShader == null) return Mats.Lit(c, smooth, metal);
            var m = new Material(ModelShader) { color = c, enableInstancing = true };
            m.SetFloat("_Glossiness", smooth);
            m.SetFloat("_Metallic", metal);
            m.SetFloat("_Edge", edge);
            m.SetFloat("_Grime", grime);
            m.SetFloat("_Foot", 0f);
            return m;
        }
    }
}
