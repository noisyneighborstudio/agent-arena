// Pezz post-processing (PezPost.cs), lean and built-in-RP only: ambient occlusion that works with the orthographic
// camera (positions rebuilt from the linear ortho depth, Alchemy-style obscurance, depth-aware blur), a bloom mip chain
// for emissives, lasers and fire, and a final pass with a hue-preserving tonemapper (Khronos PBR Neutral), a light
// grade toward the art pack's warm-sun / cool-sky palette and a soft vignette. No grain or noise (it bloats the JPEG
// streams). Lives in Resources so it ships in builds.
Shader "Hidden/Pez/Post"
{
    Properties { _MainTex ("", 2D) = "black" {} }
    CGINCLUDE
    #include "UnityCG.cginc"
    sampler2D _MainTex; float4 _MainTex_TexelSize;
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture); float4 _CameraDepthTexture_TexelSize;
    float4 _PezOrtho;   // half width, half height (world units), near, far
    float4 _PezAO;      // radius (world), intensity, power, bias
    float4 _PezBlurDir; // xy: texel step of the source
    float4 _PezBloom;   // threshold, knee, clamp, intensity
    float4 _PezGrade;   // exposure, contrast, saturation, vignette
    float4 _PezShadowTint, _PezHighTint, _PezAOTint;
    sampler2D _PezAOTex, _PezBloomTex;
    float _PezUseAO, _PezDebug, _PezToe, _PezDesat;

    struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
    v2f vert(appdata_img v) { v2f o; o.pos = UnityObjectToClipPos(v.vertex); o.uv = v.texcoord; return o; }

    float RawDepth(float2 uv) { return SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0)); }
    // Orthographic depth is linear in the depth buffer: no perspective divide to undo.
    float OrthoDepth(float raw)
    {
    #if defined(UNITY_REVERSED_Z)
        raw = 1.0 - raw;
    #endif
        return lerp(_PezOrtho.z, _PezOrtho.w, raw);
    }
    bool IsFar(float raw)
    {
    #if defined(UNITY_REVERSED_Z)
        return raw <= 1e-6;
    #else
        return raw >= 1.0 - 1e-6;
    #endif
    }
    // Snapped to a depth texel's centre: depth isn't filtered, and a lookup that lands on a texel boundary picks one
    // neighbour or the other by rounding, which (with positions taken at the unsnapped point) bends flat ground into
    // bands. The quarter-texel bias keeps half-resolution pixel centres (exactly on a boundary) off the tie.
    float2 Snap(float2 uv) { float2 sz = _CameraDepthTexture_TexelSize.zw; return (floor(uv * sz - 0.25) + 0.5) / sz; }
    float3 ViewPos(float2 uv)
    {
        uv = Snap(uv);
        return float3((uv * 2 - 1) * _PezOrtho.xy, OrthoDepth(RawDepth(uv)));
    }
    float Ign(float2 px) { return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715)))); } // interleaved gradient noise

    // ---- ambient occlusion (half resolution)
    float4 fragAO(v2f i) : SV_Target
    {
        float2 uv0 = Snap(i.uv);
        if (IsFar(RawDepth(uv0))) return 1;
        float3 P = ViewPos(uv0);
        float2 tx = _CameraDepthTexture_TexelSize.xy;
        float3 pr = ViewPos(uv0 + float2(tx.x, 0)), pl = ViewPos(uv0 - float2(tx.x, 0));
        float3 pu = ViewPos(uv0 + float2(0, tx.y)), pd = ViewPos(uv0 - float2(0, tx.y));
        // The flatter neighbour on each axis, so silhouettes don't bend the normal.
        float3 dx = abs(pr.z - P.z) < abs(P.z - pl.z) ? pr - P : P - pl;
        float3 dy = abs(pu.z - P.z) < abs(P.z - pd.z) ? pu - P : P - pd;
        float3 N = normalize(cross(dy, dx));
        if (N.z > 0) N = -N; // face the camera (view z grows away from it)
        float R = _PezAO.x;
        float2 toUv = 1.0 / (2.0 * _PezOrtho.xy);
        float rot = Ign(i.pos.xy) * 6.2831853;
        float sum = 0;
        #if defined(PEZ_AO_LOW)
        const int Count = 6;
        #else
        const int Count = 12;
        #endif
        [unroll]
        for (int k = 0; k < Count; k++)
        {
            float a = rot + k * 2.3999632; // golden angle spiral
            float r = sqrt((k + 0.5) / Count) * R;
            float2 suv = uv0 + float2(cos(a), sin(a)) * r * toUv;
            float3 v = ViewPos(suv) - P;
            float vv = dot(v, v);
            float occ = max(0, dot(v, N) - _PezAO.w) / (vv + 0.02);
            sum += occ * saturate(1.0 - vv / (R * R * 3.0)); // far occluders (a roof above open ground) don't count
        }
        float ao = saturate(1.0 - _PezAO.y * 2.0 * sum / Count);
        return pow(ao, _PezAO.z);
    }

    // Depth-aware blur, run once across and once down.
    float4 fragBlur(v2f i) : SV_Target
    {
        float zc = OrthoDepth(RawDepth(Snap(i.uv)));
        float s = 0, w = 0;
        [unroll]
        for (int k = -3; k <= 3; k++)
        {
            float2 uv = i.uv + _PezBlurDir.xy * k;
            float z = OrthoDepth(RawDepth(Snap(uv)));
            float wk = exp(-k * k * 0.18) * exp(-abs(z - zc) * 6.0);
            s += tex2Dlod(_MainTex, float4(uv, 0, 0)).r * wk; w += wk;
        }
        return s / w;
    }

    // ---- bloom
    float3 Threshold(float3 c)
    {
        c = min(c, _PezBloom.z);
        float br = max(c.r, max(c.g, c.b));
        float soft = clamp(br - _PezBloom.x + _PezBloom.y, 0, 2 * _PezBloom.y);
        soft = soft * soft / (4 * _PezBloom.y + 1e-4);
        float contrib = max(soft, br - _PezBloom.x) / max(br, 1e-4);
        return c * contrib;
    }
    float3 Box4(float2 uv, float2 t)
    {
        return (tex2D(_MainTex, uv + t * float2(-1, -1)).rgb + tex2D(_MainTex, uv + t * float2(1, -1)).rgb +
                tex2D(_MainTex, uv + t * float2(-1, 1)).rgb + tex2D(_MainTex, uv + t * float2(1, 1)).rgb) * 0.25;
    }
    float4 fragPrefilter(v2f i) : SV_Target { return float4(Threshold(Box4(i.uv, _MainTex_TexelSize.xy * 0.5)), 1); }
    float4 fragDown(v2f i) : SV_Target { return float4(Box4(i.uv, _MainTex_TexelSize.xy * 0.5), 1); }
    float4 fragUp(v2f i) : SV_Target
    {
        // 9-tap tent from the smaller mip, added onto the larger one (Blend One One).
        float2 t = _MainTex_TexelSize.xy;
        float3 c = tex2D(_MainTex, i.uv).rgb * 4;
        c += (tex2D(_MainTex, i.uv + float2(t.x, 0)).rgb + tex2D(_MainTex, i.uv - float2(t.x, 0)).rgb +
              tex2D(_MainTex, i.uv + float2(0, t.y)).rgb + tex2D(_MainTex, i.uv - float2(0, t.y)).rgb) * 2;
        c += tex2D(_MainTex, i.uv + t).rgb + tex2D(_MainTex, i.uv - t).rgb +
             tex2D(_MainTex, i.uv + float2(t.x, -t.y)).rgb + tex2D(_MainTex, i.uv + float2(-t.x, t.y)).rgb;
        return float4(c / 16, 1);
    }

    // ---- final: AO, bloom, tonemap, grade, vignette
    float3 PbrNeutral(float3 c)
    {
        // Khronos PBR Neutral, with two knobs turned for this game: far less highlight desaturation than the reference
        // (0.15, which adds the same white to every channel of a bright saturated colour, so sunlit team red went pink;
        // PezPost.HighlightDesat), and a gentler toe (the reference's 0.04 black offset pulls the blue out of the
        // biscuit ground and turns it orange; PezPost.Toe).
        const float start = 0.76;
        float desat = _PezDesat;
        float F = max(_PezToe, 1e-4);
        float x = min(c.r, min(c.g, c.b));
        float off = x < 2 * F ? x - x * x / (4 * F) : F;
        c -= off;
        float peak = max(c.r, max(c.g, c.b));
        if (peak < start) return c;
        const float d = 1 - start;
        float np = 1 - d * d / (peak + d - start);
        c *= np / peak;
        float g = 1 - 1 / (desat * (peak - np) + 1);
        return lerp(c, np.xxx, g);
    }

    float4 fragFinal(v2f i) : SV_Target
    {
        float3 c = tex2D(_MainTex, i.uv).rgb;
        if (_PezDebug > 0.5) // 1: AO, 2: bloom, 3: depth contours (every 0.25 units), 4: raw depth fine contours
        {
            if (_PezDebug < 1.5) return tex2D(_PezAOTex, i.uv).rrrr;
            if (_PezDebug < 2.5) return float4(tex2D(_PezBloomTex, i.uv).rgb, 1);
            if (_PezDebug < 3.5) return frac(OrthoDepth(RawDepth(i.uv)) * 4).xxxx;
            if (_PezDebug < 4.5) return frac(RawDepth(i.uv) * 200).xxxx;
            float3 P = ViewPos(i.uv);
            float2 tx = _CameraDepthTexture_TexelSize.xy;
            float3 pr = ViewPos(i.uv + float2(tx.x, 0)), pu = ViewPos(i.uv + float2(0, tx.y));
            if (_PezDebug < 5.5) return float4(normalize(cross(pu - P, pr - P)) * 0.5 + 0.5, 1); // 5: normals
            return float4(abs(pr.z - P.z) * 50, abs(pu.z - P.z) * 50, tx.x * 1000, 1);          // 6: depth steps, texel size
        }
        if (_PezUseAO > 0.5)
        {
            float ao = tex2D(_PezAOTex, i.uv).r;
            c *= lerp(_PezAOTint.rgb, 1.0, ao);
        }
        c += tex2D(_PezBloomTex, i.uv).rgb * _PezBloom.w;
        c = clamp(c, 0, 64); // an overflowed HDR pixel (a hot spark) must not go black through the curve
        c *= _PezGrade.x;
        c = 0.18 * pow(max(c, 1e-5) / 0.18, _PezGrade.y); // contrast about mid grey, before the curve
        c = PbrNeutral(c);
        float l = dot(c, float3(0.2126, 0.7152, 0.0722));
        c = max(0, lerp(l.xxx, c, _PezGrade.z));
        c *= lerp(_PezShadowTint.rgb, _PezHighTint.rgb, smoothstep(0.02, 0.5, l)); // cool shade, warm light
        float2 d = (i.uv - 0.5) * float2(_MainTex_TexelSize.y * _MainTex_TexelSize.z, 1);
        c *= 1 - _PezGrade.w * smoothstep(0.3, 0.95, length(d) * 1.25);
        return float4(c, 1);
    }
    ENDCG

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragAO
            #pragma target 3.0
            #pragma multi_compile_local _ PEZ_AO_LOW
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragBlur
            #pragma target 3.0
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragPrefilter
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDown
            ENDCG }
        Pass { Blend One One
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragUp
            ENDCG }
        Pass { CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragFinal
            #pragma target 3.0
            ENDCG }
    }
}
