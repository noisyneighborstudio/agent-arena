// Cola lakes: glossy, rippled, semi-transparent. Per-pixel ripple normals catch the sun and reflect the sky cubemap
// (Look.SkyCube) with Standard PBR fresnel; the water's thickness, read from the camera depth texture (linear for the
// orthographic camera), darkens the deep middle, lets the seabed show through at the edge and draws a pale fizz line
// where the cola meets the shore. Small moving glints and bubbles so it reads as liquid from a high camera.
Shader "Pez/Water"
{
    Properties
    {
        _Color ("Shallow", Color) = (0.15,0.45,0.55,0.75)
        _Deep ("Deep", Color) = (0.03,0.12,0.22,0.9)
        _Glossiness ("Smoothness", Range(0,1)) = 0.92
        _ShoreColor ("Shore fizz", Color) = (0.93,0.87,0.76,1)
        _ShoreWidth ("Shore fizz width (view depth)", Float) = 0.11
        _DepthScale ("Depth for full colour", Float) = 0.42
        _Ripple ("Ripple strength", Range(0,1)) = 0.28
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" }
        LOD 200
        CGPROGRAM
        #pragma surface surf Standard alpha:fade vertex:vert
        #pragma target 3.5
        struct Input { float3 worldPos; float4 screenPos; float eyeZ; };
        fixed4 _Color, _Deep, _ShoreColor; half _Glossiness; float _ShoreWidth, _DepthScale, _Ripple;
        UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.eyeZ = -UnityObjectToViewPos(v.vertex).z;
        }

        float SceneEyeDepth(float raw)
        {
            if (unity_OrthoParams.w > 0.5)
            {
            #if defined(UNITY_REVERSED_Z)
                raw = 1.0 - raw;
            #endif
                return lerp(_ProjectionParams.y, _ProjectionParams.z, raw);
            }
            return LinearEyeDepth(raw);
        }

        void surf(Input IN, inout SurfaceOutputStandard o)
        {
            float t = _Time.y;
            float2 p = IN.worldPos.xz;
            // Ripples: a few travelling waves; their slopes make the tangent-space normal (the plane's tangent is +X).
            float2 g = float2(
                cos(p.x * 1.7 + p.y * 0.6 + t * 1.2) * 0.5 + cos(p.x * 3.9 - p.y * 2.3 + t * 1.9) * 0.3 + cos(p.x * 7.3 + p.y * 5.1 - t * 2.6) * 0.15,
                sin(p.y * 1.5 - p.x * 0.4 + t * 1.0) * 0.5 + sin(p.y * 4.1 + p.x * 2.7 + t * 1.6) * 0.3 + sin(p.y * 6.7 - p.x * 4.9 + t * 2.3) * 0.15);
            o.Normal = normalize(float3(g * _Ripple, 1));

            // Thickness of the water along the view, from the depth texture (opaque scene only).
            float2 suv = IN.screenPos.xy / IN.screenPos.w;
            float thick = SceneEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, suv)) - IN.eyeZ;
            float deep = saturate(thick / _DepthScale);
            float s = sin(p.x * 0.8 + t) * sin(p.y * 0.6 + t * 0.8) * 0.5 + 0.5;
            fixed3 c = lerp(_Color.rgb, _Deep.rgb, deep * (0.75 + 0.25 * s));
            // A pale fizz line at the shore that breathes a little.
            float shore = 1.0 - saturate(thick / (_ShoreWidth * (0.85 + 0.3 * sin(t * 1.3 + p.x * 2.1 + p.y * 1.7))));
            shore *= step(0.0, thick);
            c = lerp(c, _ShoreColor.rgb, shore * 0.85);
            o.Albedo = c;
            // Moving glints and fizz bubbles, so it reads as liquid from a high orthographic camera, not as a hole.
            float g1 = sin(p.x * 3.1 + p.y * 1.7 + t * 1.6), g2 = sin(p.x * -1.3 + p.y * 2.9 + t * 1.1);
            float glint = pow(saturate(g1 * g2), 28);
            float fizz = step(0.988, frac(sin(dot(floor(p * 6 + t * 0.5), float2(12.9898, 78.233))) * 43758.5453));
            o.Emission = (glint * 0.25 + fizz * 0.2 * (1 - deep * 0.5)) * fixed3(1.0, 0.86, 0.7);
            o.Smoothness = lerp(_Glossiness, 0.5, shore);
            o.Metallic = 0;
            o.Alpha = lerp(0.55, lerp(_Color.a, _Deep.a, deep), saturate(thick / 0.08)) * step(0.0, thick) + shore * 0.35;
        }
        ENDCG
    }
}
