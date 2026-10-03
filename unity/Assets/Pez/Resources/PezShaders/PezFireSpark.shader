// Embers and sparks from a building's fire (FireSim): GPU particles in a StructuredBuffer, stepped by PezFireSim.compute
// through the simulated flow, drawn here as streaks stretched along their motion on screen, cooling from yellow-white
// through orange to a dull red as they age. Additive and HDR, so the young ones bloom. Six vertices per particle, no mesh.
// Lives in Resources so it ships in builds.
Shader "Pez/FireSpark"
{
    Properties
    {
        _Stretch ("Stretch (s)", Float) = 0.03
        _Gain ("Brightness", Float) = 5
    }
    SubShader
    {
        Tags { "Queue"="Transparent+3" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend One One
        ZWrite Off
        ZTest LEqual
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            struct Spark { float3 pos; float life; float3 vel; float life0; float4 k; };
            StructuredBuffer<Spark> _Sparks;
            float _Stretch, _Gain;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float3 col : COLOR; };

            v2f vert(uint vid : SV_VertexID)
            {
                v2f o;
                Spark p = _Sparks[vid / 6];
                uint c = vid % 6;
                if (p.life <= 0) { o.pos = float4(0, 0, -2, 1); o.uv = 0; o.col = 0; return o; }
                float age = 1 - p.life / p.life0;
                float3 view = mul(UNITY_MATRIX_V, float4(p.pos, 1)).xyz;
                float3 vv = mul((float3x3)UNITY_MATRIX_V, p.vel);
                float speed = length(vv.xy);
                float2 axis = speed > 1e-4 ? vv.xy / speed : float2(0, 1);
                float2 side = float2(-axis.y, axis.x);
                float size = p.k.z;
                float len = size + speed * _Stretch;
                float2 cx = float2(c == 1 || c == 2 || c == 4 ? 1 : -1, c == 2 || c == 4 || c == 5 ? 0 : -1); // across, along (tail -1 .. head 0)
                view.xy += axis * (cx.y * len + size * 0.5) + side * cx.x * size * 0.5 * (1 - 0.4 * age);
                o.pos = mul(UNITY_MATRIX_P, float4(view, 1));
                o.uv = cx;
                float3 hot = age < 0.3 ? lerp(float3(1.0, 0.92, 0.65), float3(1.0, 0.55, 0.14), age / 0.3) : lerp(float3(1.0, 0.55, 0.14), float3(0.55, 0.09, 0.02), (age - 0.3) / 0.7);
                float flick = 0.75 + 0.25 * sin(p.k.w * 90 + p.life * 30);
                float fade = saturate(p.life / max(0.15 * p.life0, 1e-3));
                o.col = min(hot * _Gain * (1.2 - age) * flick * fade, 8.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float a = (1 - i.uv.x * i.uv.x) * smoothstep(-1.0, -0.5, i.uv.y);
                return fixed4(i.col * a, 0);
            }
            ENDCG
        }
    }
}
