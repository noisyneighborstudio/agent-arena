// Animated, glossy, semi-transparent water surface.
Shader "Pez/Water"
{
    Properties
    {
        _Color ("Shallow", Color) = (0.15,0.45,0.55,0.75)
        _Deep ("Deep", Color) = (0.03,0.12,0.22,0.9)
        _Glossiness ("Smoothness", Range(0,1)) = 0.92
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard alpha:fade vertex:vert
        #pragma target 3.0
        struct Input { float3 worldPos; };
        fixed4 _Color; fixed4 _Deep; half _Glossiness;
        void vert(inout appdata_full v)
        {
            float3 wp = mul(unity_ObjectToWorld, v.vertex).xyz;
            float t = _Time.y;
            float dx = cos(wp.x * 1.3 + t * 1.1) * 0.12 + cos(wp.z * 0.7 + t * 0.6) * 0.08;
            float dz = sin(wp.z * 1.1 + t * 0.9) * 0.12 + sin(wp.x * 0.9 + t * 0.7) * 0.08;
            v.normal = normalize(float3(dx, 1, dz));
        }
        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float s = sin(IN.worldPos.x * 0.8 + _Time.y) * sin(IN.worldPos.z * 0.6 + _Time.y * 0.8) * 0.5 + 0.5;
            fixed4 c = lerp(_Deep, _Color, s * 0.5 + 0.25);
            o.Albedo = c.rgb;
            o.Smoothness = _Glossiness;
            o.Metallic = 0;
            o.Alpha = c.a;
        }
        ENDCG
    }
}
