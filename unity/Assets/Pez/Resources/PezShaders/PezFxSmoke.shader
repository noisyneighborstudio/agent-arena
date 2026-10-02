// Smoke and dust puffs, lit by the scene: each billboard is shaded as a lumpy sphere by the main directional light and
// the ambient probe, so plumes have a bright sunlit side and a dark underside instead of reading as flat discs. Young
// explosion smoke glows from inside (_Heat) and cools to plain smoke over the first _HeatFade of its life: the fireball
// turning into smoke. Premultiplied alpha. Vertex streams set by FxSystems: Position, Color, UV (TEXCOORD0.xy),
// AgePercent (TEXCOORD0.z), StableRandomX (TEXCOORD0.w). Lives in Resources so it ships in builds.
Shader "Pez/FxSmoke"
{
    Properties
    {
        _Heat ("Heat glow", Float) = 0
        _HeatFade ("Heat fade (fraction of life)", Float) = 0.25
        _Noise ("Edge noise", Float) = 0.6
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            float _Heat, _HeatFade, _Noise;
            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float4 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float4 uv : TEXCOORD0; float3 amb : TEXCOORD1; };

            float hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float vnoise(float2 p)
            {
                float2 i = floor(p), f = frac(p); f = f * f * (3 - 2 * f);
                return lerp(lerp(hash21(i), hash21(i + float2(1, 0)), f.x), lerp(hash21(i + float2(0, 1)), hash21(i + float2(1, 1)), f.x), f.y);
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv;
                o.amb = ShadeSH9(float4(0, 1, 0, 1)) * 0.8;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 d = i.uv.xy * 2 - 1;
                float r2 = dot(d, d);
                float age = i.uv.z;
                // Billows: two octaves of noise that drift slowly as the puff ages.
                float2 q = i.uv.xy * 2.2 + i.uv.w * 37.3 + float2(0, age * 0.7);
                float n1 = vnoise(q), n2 = vnoise(q * 2.4 + 3.3);
                float edge = sqrt(r2) - (n1 - 0.5) * _Noise - (n2 - 0.5) * _Noise * 0.4;
                float a = smoothstep(1.0, 0.4, edge) * i.color.a;
                clip(a - 0.004);
                // Sphere normal, bumped by the same noise, from view space to world space.
                float3 nv = normalize(float3(d * 1.15 + (float2(n1, n2) - 0.5) * 0.9, sqrt(saturate(1 - r2)) + 0.3));
                float3 nw = normalize(mul((float3x3)UNITY_MATRIX_I_V, nv));
                float ndl = saturate(dot(nw, _WorldSpaceLightPos0.xyz) * 0.65 + 0.35);
                float3 col = i.color.rgb * (_LightColor0.rgb * ndl + i.amb);
                float h = _Heat * saturate(1 - age / _HeatFade);
                h *= h;
                float core = saturate(1.1 - sqrt(r2));
                col += lerp(float3(0.5, 0.07, 0.01), float3(1.0, 0.6, 0.2), h) * h * core * 3;
                return fixed4(col * a, a);
            }
            ENDCG
        }
    }
}
