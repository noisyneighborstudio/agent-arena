// Explosion particles that need no lighting: the flash, the fireball, sparks, the ground shockwave ring and the scorch
// mark. Procedural shapes (no textures), premultiplied blending so one shader covers additive light (_Opacity 0),
// partly occluding fire (_Opacity between) and plain alpha-blended decals (_Opacity 1). Vertex streams set by
// FxSystems: Position, Color, UV (TEXCOORD0.xy), StableRandomX (TEXCOORD0.z). Lives in Resources so it ships in builds.
Shader "Pez/FxGlow"
{
    Properties
    {
        _Shape ("Shape: 0 glow, 1 ring, 2 blotch, 3 puff", Float) = 0
        _Opacity ("Coverage (0 additive, 1 alpha)", Range(0, 1)) = 0
        _Boost ("Brightness", Float) = 1
        _Noise ("Edge noise", Range(0, 1.5)) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "PreviewType"="Plane" }
        Blend One OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Shape, _Opacity, _Boost, _Noise;
            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; float4 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed4 color : COLOR; float3 uv : TEXCOORD0; };

            float hash21(float2 p) { p = frac(p * float2(123.34, 456.21)); p += dot(p, p + 45.32); return frac(p.x * p.y); }
            float vnoise(float2 p)
            {
                float2 i = floor(p), f = frac(p); f = f * f * (3 - 2 * f);
                return lerp(lerp(hash21(i), hash21(i + float2(1, 0)), f.x), lerp(hash21(i + float2(0, 1)), hash21(i + float2(1, 1)), f.x), f.y);
            }
            float fbm(float2 p) { return vnoise(p) * 0.62 + vnoise(p * 2.13 + 7.1) * 0.38; }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = v.uv.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 d = i.uv.xy * 2 - 1;
                float r = length(d);
                float n = _Noise > 0 ? fbm(i.uv.xy * 3.1 + i.uv.z * 61.7) - 0.5 : 0;
                float a;
                if (_Shape < 0.5) { float rr = saturate(1 - r - n * _Noise * 0.8); a = rr * rr; }                     // soft glow
                else if (_Shape < 1.5) { float rr = r + n * _Noise * 0.3; float k = (rr - 0.76) / 0.15; a = exp(-k * k) * saturate((1 - r) * 10); } // ring
                else if (_Shape < 2.5) { float rr = r + n * _Noise; a = smoothstep(1.0, 0.3, rr) * (0.7 + 0.3 * smoothstep(0.7, 0.0, r)); } // blotch
                else { float rr = r + n * _Noise; a = smoothstep(1.0, 0.2, rr); }                                  // puff
                a *= i.color.a;
                return fixed4(i.color.rgb * a * _Boost, a * _Opacity);
            }
            ENDCG
        }
    }
}
