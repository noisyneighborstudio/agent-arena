// Fog of war: one texel per tile (R: how fogged, 0 clear .. ~1 never seen), smoothed on the CPU and sampled with bilinear
// filtering, so edges fall off per pixel instead of along the fog mesh's triangles. _Color is the fog's tint.
Shader "Pez/Fog"
{
    Properties
    {
        _FogTex ("Fog", 2D) = "black" {}
        _Color ("Tint", Color) = (0,0,0,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _FogTex;
            fixed4 _Color;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
            v2f vert(appdata v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.uv; return o; }
            fixed4 frag(v2f i) : SV_Target { return fixed4(_Color.rgb, tex2D(_FogTex, i.uv).r); }
            ENDCG
        }
    }
}
