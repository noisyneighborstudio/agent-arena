// Health and fuel bars over units and buildings. A camera-facing quad drawn in view space from the object's origin, so
// every camera (the main window, each player's stream, look stills) shows them, whatever its yaw. Drawn on top of the
// world; a licorice track, the fill in the HUD kit's status colour, and the hazard stripe when _Hazard is set.
// Lives in Resources so it ships in builds.
Shader "Pez/Bar"
{
    Properties
    {
        _Fill ("Fill", Range(0, 1)) = 1
        _FillColor ("Fill colour", Color) = (0.93, 0.89, 0.82, 1)
        _BackColor ("Track colour", Color) = (0.118, 0.106, 0.114, 0.9)
        _Size ("Width, height, lift (world units)", Vector) = (0.9, 0.11, 0, 0)
        _Hazard ("Hazard stripe", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Overlay" "RenderType"="Transparent" "IgnoreProjector"="True" "DisableBatching"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        ZTest Always
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            float _Fill; fixed4 _FillColor, _BackColor; float4 _Size; float _Hazard;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                float3 c = UnityObjectToViewPos(float3(0, 0, 0));
                c.xy += v.vertex.xy * _Size.xy + float2(0, _Size.z);
                o.pos = mul(UNITY_MATRIX_P, float4(c, 1));
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // A thin licorice border all round, then the fill from the left.
                float by = 0.18, bx = by * _Size.y / _Size.x;
                if (i.uv.y < by || i.uv.y > 1 - by || i.uv.x < bx || i.uv.x > 1 - bx) return _BackColor;
                float x = (i.uv.x - bx) / (1 - 2 * bx);
                if (x > _Fill) return _BackColor;
                if (_Hazard > 0.5)
                {
                    float s = frac((i.uv.x * _Size.x + i.uv.y * _Size.y) / (_Size.y * 1.6));
                    return s < 0.5 ? fixed4(0.118, 0.106, 0.114, 1) : _FillColor;
                }
                return _FillColor;
            }
            ENDCG
        }
    }
}
