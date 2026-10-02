using System.Collections.Generic;
using UnityEngine;

namespace Pez.View
{
    /// <summary>
    /// Runtime material factory. Base materials live in Resources (created by PezSetup) so their
    /// shaders and keyword variants are guaranteed to be included in player builds.
    /// </summary>
    public static class Mats
    {
        static readonly Dictionary<string, Material> cache = new Dictionary<string, Material>();
        static Material Base(string name)
        {
            var key = "base:" + name;
            if (cache.TryGetValue(key, out var m)) return m;
            m = Resources.Load<Material>("PezMaterials/" + name);
            if (m == null)
            {
                Debug.LogWarning($"Missing Resources/PezMaterials/{name}; run Pez > Setup Project. Falling back to Shader.Find.");
                var shader = name switch
                {
                    "Lit" => Shader.Find("Standard"),
                    "LitEmissive" => Shader.Find("Standard"),
                    "Terrain" => Shader.Find("Pez/VertexColorLit"),
                    "Water" => Shader.Find("Pez/Water"),
                    _ => Shader.Find("Pez/Unlit"),
                };
                m = new Material(shader);
            }
            cache[key] = m;
            return m;
        }

        public static Material Lit(Color c, float smooth = 0.35f, float metal = 0.1f) => Get($"lit:{c}:{smooth}:{metal}", () =>
        {
            var m = new Material(Base("Lit")) { color = c, enableInstancing = true };
            m.SetFloat("_Glossiness", smooth);
            m.SetFloat("_Metallic", metal);
            m.SetColor("_EmissionColor", Color.black);
            return m;
        });

        public static Material Glow(Color c, float intensity = 2f) => Get($"glow:{c}:{intensity}", () =>
        {
            var m = new Material(Base("LitEmissive")) { color = c * 0.3f, enableInstancing = true };
            m.EnableKeyword("_EMISSION");
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            m.SetColor("_EmissionColor", c * intensity);
            return m;
        });

        public static Material Terrain() => Base("Terrain");
        public static Material Water() => Base("Water");

        /// <summary>Unlit vertex-coloured material; additive or alpha-blended.</summary>
        public static Material Unlit(Color c, bool additive = false, bool overlay = false) => Get($"unlit:{c}:{additive}:{overlay}", () =>
        {
            var m = new Material(Base("Unlit")) { color = c, enableInstancing = true };
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", additive ? (float)UnityEngine.Rendering.BlendMode.One : (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (overlay) { m.SetFloat("_ZTest", (float)UnityEngine.Rendering.CompareFunction.Always); m.renderQueue = 3500; }
            return m;
        });

        /// <summary>A fresh (uncached) instance for things that animate their own colour.</summary>
        public static Material UnlitInstance(Color c, bool additive) => new Material(Unlit(c, additive));

        static Material Get(string key, System.Func<Material> make)
        {
            if (!cache.TryGetValue(key, out var m)) cache[key] = m = make();
            return m;
        }

        // Team flavours from the art pack's palette: Blueberry, Cherry, Lime, Lemon.
        // Open arenas seat up to eight: four more flavours beyond the art pack's original four.
        public static readonly Color[] TeamColors =
        {
            PezPalette.TeamBlue, PezPalette.TeamRed, PezPalette.TeamGreen, PezPalette.TeamYellow,
            new Color32(142, 68, 217, 255), new Color32(255, 138, 31, 255), new Color32(46, 211, 183, 255), new Color32(232, 62, 140, 255),
        };
        public static readonly string[] TeamNames = { "Blueberry", "Cherry", "Lime", "Lemon", "Grape", "Orange", "Mint", "Raspberry" };
        public static Color Team(int t) => t >= 0 && t < TeamColors.Length ? TeamColors[t] : Color.white;
    }
}
