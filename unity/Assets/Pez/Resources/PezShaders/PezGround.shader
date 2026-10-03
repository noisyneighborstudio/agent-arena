// The Sugar Flats ground (art pack 05_Terrain / hero): vertex colours carry the large soft blotches, the ore pads and
// the lake shore; this adds the small cream speckles and the faint tile grid, plus (quality pass) gentle sun-lit relief:
// shallow dunes whose slope TerrainView bakes into UV1, shaded against the sun's direction here. TerrainView also bakes a
// broad variation in value and warmth into the vertex colours. Both stay inside the art pack's 8% quiet-ground budget
// and are low frequency, so they cost the JPEG streams next to nothing.
// Lives in Resources so it ships in builds.
Shader "Pez/Ground"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.08
        _Metallic ("Metallic", Range(0,1)) = 0
        _GridStrength ("Grid Strength", Range(0,1)) = 0.07
        _GridPeriod ("Grid Period (tiles)", Float) = 2
        _GridWidth ("Grid Line Width (tiles)", Range(0,0.2)) = 0.03
        _SpeckColor ("Speckle Colour", Color) = (0.925,0.894,0.824,1)
        _SpeckStrength ("Speckle Strength", Range(0,1)) = 0.45
        _SpeckDensity ("Speckle Density", Range(0,1)) = 0.4
        _SpeckSize ("Speckle Half Size (tiles)", Range(0,0.3)) = 0.05
        _Relief ("Sun-lit relief", Range(0,0.2)) = 0.045
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0
        struct Input { float4 vcolor; float3 worldPos; float2 dune; };
        fixed4 _Color; half _Glossiness; half _Metallic;
        float _GridStrength, _GridPeriod, _GridWidth;
        fixed4 _SpeckColor; float _SpeckStrength, _SpeckDensity, _SpeckSize;
        float _Relief;

        float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }

        // One jittered square dot per cell for a fraction of cells; squares read as little diamonds in the 45 degree view.
        float speck(float2 p, float scale, float density, float size, float seed)
        {
            float2 q = p * scale;
            float2 cell = floor(q);
            float2 f = q - cell;
            float on = step(hash(cell + seed), density);
            float2 c = 0.2 + 0.6 * float2(hash(cell + seed + 17.3), hash(cell + seed + 41.9));
            float2 d = abs(f - c);
            float m = max(d.x, d.y) / scale;
            float aa = max(fwidth(p.x), fwidth(p.y)) * 0.7;
            return on * (1.0 - smoothstep(size - aa, size + aa, m));
        }

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.vcolor = v.color;
            o.dune = v.texcoord1.xy; // slope of the dune height field (TerrainView.BuildGround)
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float2 p = IN.worldPos.xz;
            fixed3 c = IN.vcolor.rgb * _Color.rgb;
            // Shallow dunes lit by the sun: the baked slope against the light's ground direction (sunward slopes lighter).
            float2 L = normalize(_WorldSpaceLightPos0.xz + 1e-4);
            c *= 1 - clamp(dot(IN.dune, L) * _Relief, -_Relief, _Relief);
            // Cream speckles: a sparse even layer plus a finer, rarer one so they cluster a little, as in the hero.
            float s = max(speck(p, 1.0, _SpeckDensity, _SpeckSize, 0.0), speck(p, 1.7, _SpeckDensity * 0.35, _SpeckSize * 0.8, 91.7));
            c = lerp(c, _SpeckColor.rgb, s * _SpeckStrength);
            // The faint diamond grid: thin darker lines every _GridPeriod tiles, anti-aliased to stay about a pixel wide.
            float2 g = abs(frac(p / _GridPeriod + 0.5) - 0.5) * _GridPeriod;
            float2 fw = fwidth(p);
            float half_ = _GridWidth * 0.5;
            float lx = 1.0 - smoothstep(half_, half_ + fw.x * 1.2, g.x);
            float ly = 1.0 - smoothstep(half_, half_ + fw.y * 1.2, g.y);
            c *= 1.0 - max(lx, ly) * _GridStrength;
            o.Albedo = c;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
