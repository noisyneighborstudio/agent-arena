// Newcomer protection, drawn on the protected team's own units and buildings: a cream sheen laid over each model part
// (WorldView.SyncSheen adds a copy of the part's mesh with this material). It brightens what's already there (colour x
// (1 + sheen), so the model keeps its shading: dark parts stay dark, light ones glow): a wash, a rim on the silhouette,
// and a band that sweeps across the world on a diagonal, so a glint runs over every protected model in turn. _Strength (the breath, and the fade in and out) is set per team from the CPU. Status, so cream: never a team
// hue. Lives in Resources so it ships in builds.
Shader "Pez/Sheen"
{
    Properties
    {
        _Color ("Colour", Color) = (0.93, 0.89, 0.82, 1)
        _Strength ("Strength (breath x fade)", Range(0, 1)) = 1
        _Rim ("Rim", Float) = 0.8
        _Wash ("Wash", Float) = 0.45
        _Band ("Sweep band", Float) = 1.1
        _BandEvery ("Sweep spacing (world units)", Float) = 9
        _BandSpeed ("Sweep speed (world units/s)", Float) = 3.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend DstColor One
        ZWrite Off
        ZTest LEqual
        Offset -1, -1
        Cull Back
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            float _Strength, _Rim, _Wash, _Band, _BandEvery, _BandSpeed;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; };
            struct v2f { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 wnormal : TEXCOORD1; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnormal = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.wnormal), view = normalize(_WorldSpaceCameraPos - i.wpos);
                float rim = pow(1 - saturate(dot(n, view)), 2);
                // The sweep: a soft line across the map (x+z diagonal, leaning with height) moving every _BandEvery units.
                float s = frac(((i.wpos.x + i.wpos.z) * 0.7071 + i.wpos.y * 0.8 - _Time.y * _BandSpeed) / _BandEvery);
                float d = (s - 0.5) * _BandEvery; // world units from the band's centre
                float band = exp(-d * d * 1.8);
                float k = (_Wash + rim * _Rim + band * _Band) * _Strength;
                return fixed4(_Color.rgb * k, 0);
            }
            ENDCG
        }
    }
}
