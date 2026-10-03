using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Pez.View
{
    /// <summary>
    /// Post-processing for the main camera and the player-stream camera (shader: Resources/PezShaders/PezPost):
    /// ambient occlusion that works with the orthographic camera, bloom, a hue-preserving filmic tonemap, a light grade
    /// toward the art pack's warm sun and cool sky, and a soft vignette. Health and fuel bars are drawn after all of it
    /// (CameraEvent.AfterImageEffects), so they stay crisp and in the HUD kit's exact colours; the IMGUI HUD draws after
    /// the cameras and is never touched.
    ///
    /// Tiers (one switch each): Full (12-sample AO, 6 bloom mips), Light (6-sample AO, 4 bloom mips; the same look at
    /// a lower cost) and Off (no post at all, bars still drawn). The main view uses MainTier, the player streams use
    /// StreamTier; command line: -mainfx full|light|off, -streamfx full|light|off.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class PezPost : MonoBehaviour
    {
        public enum Tier { Off, Light, Full }
        public static Tier MainTier = Arg("-mainfx", Tier.Full);
        public static Tier StreamTier = Arg("-streamfx", Tier.Light);
        public bool Stream;
        Tier Level => Stream ? StreamTier : MainTier;

        // The look. Tuned against the art pack's renders (docs/art/boards); see docs/render/QUALITY_PASS.md.
        public static float AORadius = 0.55f, AOIntensity = 0.9f, AOPower = 1.4f, AOBias = 0.01f;
        public static Color AOTint = new Color(0.30f, 0.26f, 0.30f); // occlusion goes toward warm licorice, not grey
        public static float BloomThreshold = 2.0f, BloomKnee = 0.5f, BloomClamp = 24f, BloomIntensity = 0.26f;
        public static float Exposure = 1.06f, Contrast = 1.04f, Saturation = 1.0f, Vignette = 0.12f, Toe = 0.012f;
        /// <summary>Debug view (LookTune): 0 normal, 1 the AO buffer, 2 the bloom buffer.</summary>
        public static float Debug = 0f, AOBlur = 1f;
        public static Color ShadowTint = new Color(0.965f, 0.985f, 1.04f), HighTint = new Color(1.015f, 1.0f, 0.985f);

        Camera cam;
        Material mat;
        CommandBuffer overlay;
        static readonly int OrthoId = Shader.PropertyToID("_PezOrtho"), AOParamId = Shader.PropertyToID("_PezAO"),
            BlurDirId = Shader.PropertyToID("_PezBlurDir"), BloomParamId = Shader.PropertyToID("_PezBloom"),
            GradeId = Shader.PropertyToID("_PezGrade"), ShadowTintId = Shader.PropertyToID("_PezShadowTint"),
            HighTintId = Shader.PropertyToID("_PezHighTint"), AOTintId = Shader.PropertyToID("_PezAOTint"),
            AOTexId = Shader.PropertyToID("_PezAOTex"), BloomTexId = Shader.PropertyToID("_PezBloomTex"), UseAOId = Shader.PropertyToID("_PezUseAO");
        const int PassAO = 0, PassBlur = 1, PassPrefilter = 2, PassDown = 3, PassUp = 4, PassFinal = 5;
        static bool logged;

        static Tier Arg(string name, Tier def)
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, name);
            if (i < 0 || i + 1 >= args.Length) return def;
            return args[i + 1].ToLowerInvariant() switch { "off" => Tier.Off, "light" => Tier.Light, "full" => Tier.Full, _ => def };
        }

        void OnEnable()
        {
            cam = GetComponent<Camera>();
            cam.depthTextureMode |= DepthTextureMode.Depth; // already made for the sun's screen-space shadows; AO and water read it
            var shader = Resources.Load<Shader>("PezShaders/PezPost");
            if (shader != null && shader.isSupported) mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            else UnityEngine.Debug.LogWarning("PezPost: post shader missing or unsupported; rendering without post-processing");
            overlay = new CommandBuffer { name = "Pez overlay (bars)" };
            cam.AddCommandBuffer(CameraEvent.AfterImageEffects, overlay);
        }

        void OnDisable()
        {
            if (cam != null && overlay != null) cam.RemoveCommandBuffer(CameraEvent.AfterImageEffects, overlay);
            overlay?.Release(); overlay = null;
            if (mat != null) Destroy(mat);
        }

        // Bars render on top of everything after post, with this camera's own view (the overlay pass runs after the
        // image effects, which leave their own blit matrices bound).
        void OnPreRender()
        {
            if (overlay == null) return;
            overlay.Clear();
            overlay.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            overlay.SetViewProjectionMatrices(cam.worldToCameraMatrix, cam.projectionMatrix);
            Bars.DrawOverlay(overlay);
        }

        void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            var level = Level;
            if (mat == null || level == Tier.Off) { Graphics.Blit(src, dst); return; }
            if (!logged) { logged = true; UnityEngine.Debug.Log($"PezPost: {name} {src.width}x{src.height} {src.format} tier {level} (main {MainTier}, streams {StreamTier})"); }
            int w = src.width, h = src.height;
            float halfH = cam.orthographic ? cam.orthographicSize : Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 20f;
            mat.SetVector(OrthoId, new Vector4(halfH * cam.aspect, halfH, cam.nearClipPlane, cam.farClipPlane));

            // Ambient occlusion: only meaningful for an orthographic camera (positions come from linear ortho depth).
            bool ao = cam.orthographic;
            RenderTexture aoTex = null;
            if (ao)
            {
                // Radius in world units, but never under ~2.5 px so it doesn't turn to noise when zoomed far out.
                float pxPerUnit = h / (2f * halfH);
                float radius = Mathf.Max(AORadius, 2.5f / pxPerUnit);
                mat.SetVector(AOParamId, new Vector4(radius, AOIntensity, AOPower, AOBias));
                if (level == Tier.Light) mat.EnableKeyword("PEZ_AO_LOW"); else mat.DisableKeyword("PEZ_AO_LOW");
                // Full resolution: depth can't be filtered, and half-resolution lookups land exactly on depth texel
                // boundaries, where rounding picks either neighbour and flat ground breaks into bands.
                const int div = 1;
                aoTex = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
                var tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
                aoTex.filterMode = tmp.filterMode = FilterMode.Bilinear;
                Graphics.Blit(src, aoTex, mat, PassAO);
                if (AOBlur > 0.5f)
                {
                    mat.SetVector(BlurDirId, new Vector4(div / (float)w, 0, 0, 0));
                    Graphics.Blit(aoTex, tmp, mat, PassBlur);
                    mat.SetVector(BlurDirId, new Vector4(0, div / (float)h, 0, 0));
                    Graphics.Blit(tmp, aoTex, mat, PassBlur);
                }
                RenderTexture.ReleaseTemporary(tmp);
                mat.SetTexture(AOTexId, aoTex);
                mat.SetColor(AOTintId, AOTint);
            }
            mat.SetFloat(UseAOId, ao ? 1 : 0);

            // Bloom: threshold at half resolution, then a mip chain down and back up.
            int mips = level == Tier.Full ? 6 : 4;
            var fmt = src.format == RenderTextureFormat.ARGBHalf || src.format == RenderTextureFormat.ARGBFloat || src.format == RenderTextureFormat.RGB111110Float
                ? RenderTextureFormat.RGB111110Float : RenderTextureFormat.ARGB32;
            if (!SystemInfo.SupportsRenderTextureFormat(fmt)) fmt = RenderTextureFormat.ARGBHalf;
            mat.SetVector(BloomParamId, new Vector4(BloomThreshold, BloomKnee, BloomClamp, BloomIntensity));
            var chain = new RenderTexture[mips];
            int mw = w / 2, mh = h / 2, n = 0;
            for (; n < mips && mw >= 4 && mh >= 4; n++, mw /= 2, mh /= 2)
            {
                chain[n] = RenderTexture.GetTemporary(mw, mh, 0, fmt, RenderTextureReadWrite.Linear);
                chain[n].filterMode = FilterMode.Bilinear;
            }
            Graphics.Blit(src, chain[0], mat, PassPrefilter);
            for (int i = 1; i < n; i++) Graphics.Blit(chain[i - 1], chain[i], mat, PassDown);
            for (int i = n - 1; i > 0; i--) Graphics.Blit(chain[i], chain[i - 1], mat, PassUp);
            mat.SetTexture(BloomTexId, chain[0]);

            mat.SetVector(GradeId, new Vector4(Exposure, Contrast, Saturation, Vignette));
            mat.SetColor(ShadowTintId, ShadowTint);
            mat.SetColor(HighTintId, HighTint);
            mat.SetFloat("_PezDebug", Debug);
            mat.SetFloat("_PezToe", Toe);
            Graphics.Blit(src, dst, mat, PassFinal);

            for (int i = 0; i < n; i++) RenderTexture.ReleaseTemporary(chain[i]);
            if (aoTex != null) RenderTexture.ReleaseTemporary(aoTex);
        }
    }
}
