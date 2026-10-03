// The art pack's faceted models (glTF blockouts, boulders, cliffs), lit with Unity's Standard PBR. Look.cs swaps each
// opaque glTF material for this one with smoothness and metalness tuned per material name (gunmetal, team paint,
// cream plastic, concrete...). On top of plain PBR:
//  - chamfered edges: Models.BevelData stores, per triangle corner, the distance to each edge, the edge's chamfer
//    normal (tangent space) and a bevel width scaled to the part. Within that width of a convex crease the surface
//    takes the chamfer's normal, so the edge catches the sun and the sky like a real 45-degree chamfer instead of
//    meeting at a razor edge. Shading only: silhouettes, pivots and footprints are unchanged (at ~40 px per tile a
//    0.04 chamfer is under 2 px of silhouette);
//  - a little world-space variation in value and gloss (grime), low frequency so it doesn't cost stream bandwidth;
//  - a darker foot near the ground (the art pack's "top-down gradient", decision 12), which also grounds units;
//  - HDR emission (_EmissionColor), so the glowing parts feed the bloom;
//  - a dielectric specular scale (_SpecK): the metallic workflow's fixed 4% reflectance adds the same white to every
//    channel, which on saturated team paint reads as a pink-washed red under a strong sun. Team paint runs at about a
//    third of it, so team colours stay vivid (lit through Unity's StandardSpecular, metalness converted here);
//  - two per-renderer view states, set through a MaterialPropertyBlock (PezShade) and instanced, so batching survives:
//    _PezDim darkens everything (a wreck goes to 0.3, charred and matte) and _PezGlow scales only the emission
//    (power plant cores following the load, consumers dimmed on low power, dock lamps).
// Lives in Resources so it ships in builds.
Shader "Pez/Model"
{
    Properties
    {
        [MainColor] _Color ("Color", Color) = (1,1,1,1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.5
        [Gamma] _Metallic ("Metallic", Range(0,1)) = 0
        [HDR] _EmissionColor ("Emission", Color) = (0,0,0,1)
        _Edge ("Edge highlight", Range(0,1)) = 0.3
        _Grime ("Grime / variation", Range(0,1)) = 0.5
        _Foot ("Ground darkening", Range(0,1)) = 0.18
        _SpecK ("Dielectric specular scale", Range(0,1)) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _CullMode ("Cull", Float) = 2
        _PezDim ("View: darken (wrecks)", Range(0,1)) = 1
        _PezGlow ("View: emission scale", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 300
        Cull [_CullMode]
        CGPROGRAM
        #pragma surface surf StandardSpecular fullforwardshadows vertex:vert addshadow
        #pragma target 3.5
        #pragma multi_compile_instancing

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float4 tangent : TANGENT;
            float4 texcoord : TEXCOORD0;
            float4 texcoord1 : TEXCOORD1;
            float4 texcoord2 : TEXCOORD2;
            float4 texcoord3 : TEXCOORD3; // edge distances, bevel width
            float4 texcoord4 : TEXCOORD4; // bevel normals of edges 0 and 1 (tangent space xy)
            float4 texcoord5 : TEXCOORD5; // bevel normal of edge 2
            fixed4 color : COLOR;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };
        struct Input { float3 worldPos; float4 edge; float4 bev01; float2 bev2; };
        half _Glossiness, _Metallic, _Edge, _Grime, _Foot, _SpecK;
        half _PezEdgeScale, _PezGrimeScale; // globals (Look.EdgeScale, Look.GrimeScale)
        half4 _EmissionColor;
        UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_DEFINE_INSTANCED_PROP(fixed4, _Color)
            UNITY_DEFINE_INSTANCED_PROP(half, _PezDim)
            UNITY_DEFINE_INSTANCED_PROP(half, _PezGlow)
        UNITY_INSTANCING_BUFFER_END(Props)

        float hash3(float3 p) { p = frac(p * 0.3183099 + 0.1); p *= 17.0; return frac(p.x * p.y * p.z * (p.x + p.y + p.z)); }
        float vnoise3(float3 x)
        {
            float3 i = floor(x), f = frac(x); f = f * f * (3 - 2 * f);
            return lerp(lerp(lerp(hash3(i), hash3(i + float3(1,0,0)), f.x), lerp(hash3(i + float3(0,1,0)), hash3(i + float3(1,1,0)), f.x), f.y),
                        lerp(lerp(hash3(i + float3(0,0,1)), hash3(i + float3(1,0,1)), f.x), lerp(hash3(i + float3(0,1,1)), hash3(i + float3(1,1,1)), f.x), f.y), f.z);
        }

        void vert(inout appdata v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.edge = v.texcoord3;
            o.bev01 = v.texcoord4;
            o.bev2 = v.texcoord5.xy;
        }

        void surf(Input IN, inout SurfaceOutputStandardSpecular o)
        {
            fixed4 c = UNITY_ACCESS_INSTANCED_PROP(Props, _Color);
            half edgeK = _Edge * _PezEdgeScale, grime = _Grime * _PezGrimeScale;
            float3 wp = IN.worldPos;
            float n = vnoise3(wp * 2.3); // 0..1, low frequency
            float3 albedo = c.rgb * (1.0 + (n - 0.5) * 0.14 * grime);
            albedo *= lerp(1.0 - _Foot, 1.0, saturate(wp.y / 0.45));
            float3 nrm = float3(0, 0, 1);
            float bevel = 0;
            float width = IN.edge.w * saturate(_PezEdgeScale);
            if (width > 0)
            {
                // The nearest bevelled edge, and how far inside its chamfer band this pixel is (anti-aliased).
                float3 d = IN.edge.xyz;
                float2 b = IN.bev01.xy; float dm = d.x;
                if (d.y < dm) { dm = d.y; b = IN.bev01.zw; }
                if (d.z < dm) { dm = d.z; b = IN.bev2; }
                float aa = max(fwidth(dm), 1e-4);
                bevel = saturate((width - dm) / aa + 0.5) * step(0.1, dot(b, b));
                float3 bn = float3(b, sqrt(saturate(1 - dot(b, b))));
                nrm = normalize(lerp(nrm, bn, bevel));
            }
            o.Normal = nrm;
            albedo = lerp(albedo, albedo * 1.25 + 0.03, bevel * edgeK); // a worn, slightly lighter edge
            // Metallic to specular, as Unity's DiffuseAndSpecularFromMetallic, with the dielectric reflectance scaled.
            float f0 = 0.04 * _SpecK;
            half dim = UNITY_ACCESS_INSTANCED_PROP(Props, _PezDim);
            o.Specular = lerp(f0.xxx, albedo, _Metallic) * dim;
            o.Albedo = albedo * (1 - f0) * (1 - _Metallic) * dim;
            o.Smoothness = saturate(_Glossiness * (1.0 - (n - 0.5) * 0.35 * grime) + bevel * 0.08) * lerp(0.6, 1.0, dim);
            o.Emission = _EmissionColor.rgb * (dim * UNITY_ACCESS_INSTANCED_PROP(Props, _PezGlow));
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
