// Per-renderer view states on Pez/Model materials, set through one shared MaterialPropertyBlock (no allocation):
//  - Dim: darkens albedo, specular and emission (a wreck charred to 0.3);
//  - Glow: scales the emission only (power plant cores, consumers on low power, dock lamps).
// Both are instanced properties in PezModel.shader, so instanced batching keeps working.
using UnityEngine;

namespace Pez
{
    public static class PezShade
    {
        static readonly int DimId = Shader.PropertyToID("_PezDim"), GlowId = Shader.PropertyToID("_PezGlow");
        static MaterialPropertyBlock block;

        public static void Set(Renderer r, float dim, float glow)
        {
            if (r == null) return;
            block ??= new MaterialPropertyBlock();
            r.GetPropertyBlock(block);
            block.SetFloat(DimId, dim);
            block.SetFloat(GlowId, glow);
            r.SetPropertyBlock(block);
        }

        public static void Set(Renderer[] rs, float dim, float glow)
        {
            if (rs == null) return;
            for (int i = 0; i < rs.Length; i++) Set(rs[i], dim, glow);
        }

        /// <summary>The renderers whose material is an emissive (M_E_*), e.g. a power plant's cores or a building's lamps.</summary>
        public static Renderer[] Emissive(GameObject go)
        {
            var list = new System.Collections.Generic.List<Renderer>();
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer) continue;
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.name.StartsWith("M_E_")) { list.Add(r); break; }
            }
            return list.ToArray();
        }
    }
}
