// Lit surface shader that multiplies vertex colour into albedo. Used for the procedural terrain.
Shader "Pez/VertexColorLit"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _Glossiness ("Smoothness", Range(0,1)) = 0.15
        _Metallic ("Metallic", Range(0,1)) = 0
        _DetailScale ("Detail Scale", Float) = 1.7
        _DetailStrength ("Detail Strength", Range(0,1)) = 0.12
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.0
        struct Input { float4 vcolor; float3 worldPos; };
        fixed4 _Color; half _Glossiness; half _Metallic; float _DetailScale; float _DetailStrength;

        float hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
        float noise(float2 p)
        {
            float2 i = floor(p); float2 f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(hash(i), hash(i + float2(1,0)), u.x), lerp(hash(i + float2(0,1)), hash(i + float2(1,1)), u.x), u.y);
        }
        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.vcolor = v.color;
        }
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            // Two octaves of value noise break up the flat vertex colours into something ground-like.
            float n = noise(IN.worldPos.xz * _DetailScale) * 0.6 + noise(IN.worldPos.xz * _DetailScale * 4.3) * 0.4;
            fixed4 c = IN.vcolor * _Color;
            o.Albedo = c.rgb * (1.0 - _DetailStrength + n * _DetailStrength * 2.0);
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = 1;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
