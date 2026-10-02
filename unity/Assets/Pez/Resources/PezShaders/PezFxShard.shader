// Debris shards and dirt clods: faceted low-poly mesh particles, flat-lit by the main light and ambient, in the
// particle's colour (the unit's team colour, hull plastic, ground). Vertex colour alpha marks a hot fragment that glows
// orange for the first part of its life. Casts shadows. Vertex streams set by FxSystems: Position, Normal, Color,
// AgePercent (TEXCOORD0.x). Lives in Resources so it ships in builds.
Shader "Pez/FxShard"
{
    Properties { }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; fixed4 color : COLOR; float4 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; fixed3 col : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 nw = normalize(UnityObjectToWorldNormal(v.normal));
                float ndl = abs(dot(nw, _WorldSpaceLightPos0.xyz)); // two-sided: the facets catch the light whichever way they land
                float3 lit = v.color.rgb * (_LightColor0.rgb * ndl * 0.85 + ShadeSH9(float4(nw, 1)));
                float hot = v.color.a * saturate(1 - v.uv.x * 6);
                o.col = lit + float3(1.0, 0.42, 0.1) * hot * hot * 2.5;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target { return fixed4(i.col, 1); }
            ENDCG
        }
        Pass
        {
            Tags { "LightMode"="ShadowCaster" }
            Cull Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_shadowcaster
            #include "UnityCG.cginc"
            struct v2f { V2F_SHADOW_CASTER; };
            v2f vert(appdata_base v) { v2f o; TRANSFER_SHADOW_CASTER_NORMALOFFSET(o) return o; }
            float4 frag(v2f i) : SV_Target { SHADOW_CASTER_FRAGMENT(i) }
            ENDCG
        }
    }
}
