// A building's fire drawn from its simulation (FireSim): a box around the building, raymarched front to back through
// the 3D fields. Flames are emission coloured by temperature (deep red through orange to a yellow-white core, HDR so
// the cores bloom); soot absorbs (black smoke); steam and pale smoke scatter the sun and sky (white smoke), and all
// smoke is lit from inside by the fire below it. The march stops at the scene's depth (units, the ground) and at the
// building itself (the signed distance), so the fire wraps the building and things in front of it stay in front.
// A rising noise (a baked 3D texture, FireSim.NoiseTexture) bends each lookup by a couple of cells, for flame detail finer than the grid (Ignitement's parallax
// trick plays the same part: detail the simulation doesn't pay for). Premultiplied alpha.
// Lives in Resources so it ships in builds.
Shader "Pez/FireVolume"
{
    Properties
    {
        _FlameGain ("Flame brightness", Float) = 7
        _SootK ("Soot density", Float) = 7
        _SteamK ("Steam density", Float) = 4.5
        _Detail ("Detail warp (cells)", Float) = 2.2
    }
    SubShader
    {
        Tags { "Queue"="Transparent+2" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Blend One OneMinusSrcAlpha
        ZWrite Off
        ZTest LEqual
        Cull Back
        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            sampler3D _Scal, _Sdf, _FireNoise;
            float3 _Origin, _Size, _DimF;
            float _FlameGain, _SootK, _SteamK, _Detail, _FireTime;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            struct v2f { float4 pos : SV_POSITION; float3 wp : TEXCOORD0; float4 screen : TEXCOORD1; float3 amb : TEXCOORD2; };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.wp = mul(unity_ObjectToWorld, vertex).xyz;
                o.screen = ComputeScreenPos(o.pos);
                o.amb = ShadeSH9(float4(0, 1, 0, 1));
                return o;
            }

            // Flame colour by temperature: a stylised blackbody in the art's warm caramel range.
            float3 FlameColor(float t)
            {
                float3 a = float3(0.5, 0.035, 0.0), b = float3(1.0, 0.2, 0.015), c = float3(1.0, 0.48, 0.06), d = float3(1.0, 0.82, 0.4);
                return t < 0.35 ? lerp(a, b, t / 0.35) : t < 0.7 ? lerp(b, c, (t - 0.35) / 0.35) : lerp(c, d, saturate((t - 0.7) / 0.3));
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // The ray: parallel to the view for the orthographic game camera, from the eye otherwise.
                float3 fwd = -UNITY_MATRIX_V[2].xyz;
                bool ortho = unity_OrthoParams.w > 0.5;
                float3 rd = ortho ? fwd : normalize(i.wp - _WorldSpaceCameraPos);
                float3 ro = i.wp - rd * dot(_Size, 1.0);
                float3 inv = 1.0 / rd;
                float3 ta = (_Origin - ro) * inv, tb = (_Origin + _Size - ro) * inv;
                float3 tn = min(ta, tb), tf = max(ta, tb);
                float t0 = max(max(tn.x, tn.y), tn.z), t1 = min(min(tf.x, tf.y), tf.z);
                if (!ortho) t0 = max(t0, length(i.wp - ro) - length(i.wp - _WorldSpaceCameraPos));
                // Stop at whatever opaque thing is in the way (depth of the scene behind this pixel).
                float2 uv = i.screen.xy / i.screen.w;
                float raw = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv);
                float eye;
                if (ortho)
                {
                    #if defined(UNITY_REVERSED_Z)
                    raw = 1 - raw;
                    #endif
                    eye = lerp(_ProjectionParams.y, _ProjectionParams.z, raw);
                }
                else eye = LinearEyeDepth(raw);
                float tScene = (eye - dot(ro - _WorldSpaceCameraPos, fwd)) / max(dot(rd, fwd), 1e-4);
                t1 = min(t1, tScene);
                if (t1 <= t0) discard;

                float len = t1 - t0;
                float cell = _Size.x / _DimF.x;
                int n = (int)clamp(len / cell, 10, 80);
                float stepLen = len / n;
                // A per-pixel dither on the start, so the steps don't band (static: a moving dither would cost the JPEG
                // streams bandwidth; a hash, not interleaved gradient noise, whose diagonal hatching showed in flames).
                float2 sp = floor(i.pos.xy);
                float jit = frac(sin(dot(sp, float2(12.9898, 78.233))) * 43758.5453);
                float3 L = _WorldSpaceLightPos0.xyz;
                float3 sun = _LightColor0.rgb;
                float3 col = 0;
                float tr = 1;
                [loop] for (int k = 0; k < n; k++)
                {
                    float3 p = ro + rd * (t0 + (k + jit) * stepLen);
                    float3 uvw = (p - _Origin) / _Size;
                    if (tex3Dlod(_Sdf, float4(uvw, 0)).r < -0.3) break; // into the building
                    float3 q = p * 0.55 + float3(0, -_FireTime * 0.31, 0); // the 32-cell noise tile spans ~1.8 tiles
                    float3 warp = tex3Dlod(_FireNoise, float4(q, 0)).rgb - 0.5;
                    float4 s = tex3Dlod(_Scal, float4(uvw + warp * (_Detail / _DimF), 0));
                    float T = s.x, soot = s.y, steam = s.z, R = s.w;
                    // Flame: burning gas, hotter at its core; thin wisps at its edge stay deep red.
                    float heat = saturate(T * 0.7 + R * 0.35);
                    // A defined edge: thin burning gas fades out fast, so tongues read as shapes, not haze.
                    float flame = smoothstep(0.15, 0.45, R);
                    float3 em = FlameColor(heat) * flame * (0.3 + 3.5 * heat * heat) * _FlameGain;
                    float sigma = soot * _SootK + steam * _SteamK + flame * 3.0;
                    float a = 1 - exp(-sigma * stepLen);
                    float3 scat = 0;
                    if (a > 0.002)
                    {
                        // Light reaching this bit of smoke: the sun through the smoke above it (two taps), the sky, and
                        // the fire's glow from inside.
                        float4 s1 = tex3Dlod(_Scal, float4(uvw + L * (0.4 / _Size), 0));
                        float od = (s1.y * _SootK + s1.z * _SteamK) * 0.55;
                        float pale = steam / max(soot + steam, 1e-4);
                        float3 albedo = lerp(float3(0.045, 0.038, 0.034), float3(0.93, 0.9, 0.84), pale);
                        // The fire's glow on its own smoke: deep orange, taken up in proportion to the smoke's albedo
                        // (black soot only warms a little underneath; steam glows).
                        float3 glow = FlameColor(saturate(T * 0.55)) * saturate(T * 1.4) * 1.6;
                        scat = albedo * (sun * exp(-od) * 0.9 + i.amb * 0.85 + glow) + glow * 0.06;
                    }
                    col += tr * (em * stepLen + scat * a);
                    tr *= 1 - a;
                    if (tr < 0.01) break;
                }
                float alpha = 1 - tr;
                if (alpha < 0.002 && dot(col, 1) < 0.002) discard;
                return fixed4(col, alpha);
            }
            ENDCG
        }
    }
}
