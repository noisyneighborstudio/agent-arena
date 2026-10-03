// The art pack's faceted models (glTF blockouts, boulders, cliffs), lit with Unity's Standard PBR. Look.cs swaps each
// opaque glTF material for this one with smoothness and metalness tuned per material name (gunmetal, team paint,
// cream plastic, concrete...). On top of plain PBR:
//  - a bevel highlight along convex creases: Models.Unweld stores per-corner barycentrics in UV3 (w = 1 when present,
//    coplanar and concave edges masked out), drawn about a pixel wide whatever the zoom, so chunky shapes read crisply;
//  - a little world-space variation in value and gloss (grime), low frequency so it doesn't cost stream bandwidth;
//  - a darker foot near the ground (the art pack's "top-down gradient", decision 12), which also grounds units;
//  - HDR emission (_EmissionColor), so the glowing parts feed the bloom.
// Lives in Resources so it ships in builds.
Shader "Pez/Model"
{
    Properties
    {
        [MainColor] _Color ("Color", Color) = (1,1,1,1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        [Gamma] _Metallic ("Metallic", Range(0,1)) = 0
        [HDR] _EmissionColor ("Emission", Color) = (0,0,0,1)
        _Edge ("Bevel highlight", Range(0,1)) = 0.3
        _Grime ("Grime / variation", Range(0,1)) = 0.5
        _Foot ("Ground darkening", Range(0,1)) = 0.18
        [Enum(UnityEngine.Rendering.CullMode)] _CullMode ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 300
        Cull [_CullMode]
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert addshadow
        #pragma target 3.5
        #pragma multi_compile_instancing

        struct Input { float3 worldPos; float4 edge; };
        half _Glossiness, _Metallic, _Edge, _Grime, _Foot;
        half _PezEdgeScale, _PezGrimeScale; // globals (Look.EdgeScale, Look.GrimeScale)
        half4 _EmissionColor;
        UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
        UNITY_INSTANCING_BUFFER_END(Props)

        float hash3(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }
        float vnoise3(float3 x)
        {
            float3 i = floor(x), f = frac(x); f = f * f * (3 - 2 * f);
            return lerp(lerp(lerp(hash3(i), hash3(i + float3(1,0,0)), f.x), lerp(hash3(i + float3(0,1,0)), hash3(i + float3(1,1,0)), f.x), f.y),
                        lerp(lerp(hash3(i + float3(0,0,1)), hash3(i + float3(1,0,1)), f.x), lerp(hash3(i + float3(0,1,1)), hash3(i + float3(1,1,1)), f.x), f.y), f.z);
        }

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.edge = v.texcoord3;
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            fixed4 c = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
            half edgeK = _Edge * _PezEdgeScale, grime = _Grime * _PezGrimeScale;
            float3 wp = IN.worldPos;
            float n = vnoise3(wp * 2.3); // 0..1, low frequency
            float3 albedo = c.rgb * (1.0 + (n - 0.5) * 0.14 * grime);
            albedo *= lerp(1.0 - _Foot, 1.0, saturate(wp.y / 0.45));
            float edge = 0;
            if (IN.edge.w > 0.5)
            {
                float3 b = IN.edge.xyz;
                float3 px = b / max(fwidth(b), 1e-5);
                edge = saturate(1.6 - min(px.x, min(px.y, px.z)));
            }
            albedo = lerp(albedo, albedo * 1.6 + 0.045, edge * edgeK);
            o.Albedo = albedo;
            o.Metallic = _Metallic;
            o.Smoothness = saturate(_Glossiness * (1.0 - (n - 0.5) * 0.35 * grime) + edge * edgeK * 0.15);
            o.Emission = _EmissionColor.rgb;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
