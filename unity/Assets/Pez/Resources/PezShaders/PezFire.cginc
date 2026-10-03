// The burning buildings' light in the scene (FireSim's light map, after Ignitement's): one world-space texture over
// the map, rgb the fires' light and a the shadow of their smoke on the ground, sampled once per pixel at the surface
// pushed out along its normal, which is what gives the light its direction (faces toward a fire take more of it).
// Fires a view can't see (fog) are masked out by rectangle. Included by Pez/Model and Pez/Ground.
#ifndef PEZ_FIRE_INCLUDED
#define PEZ_FIRE_INCLUDED
sampler2D _PezFireMap;
float4 _PezFireMapST;
float _PezFireOn;
float4 _PezFireRects[8];
float _PezFireVis[8];

float4 PezFireLight(float3 wp, float3 wn)
{
    if (_PezFireOn < 0.5) return 0;
    float3 p = wp + wn * 0.4;
    float4 f = tex2Dlod(_PezFireMap, float4(p.xz * _PezFireMapST.xy + _PezFireMapST.zw, 0, 0));
    [unroll] for (int i = 0; i < 8; i++)
    {
        float4 r = _PezFireRects[i];
        if (_PezFireVis[i] < 0.5 && p.x >= r.x && p.z >= r.y && p.x <= r.z && p.z <= r.w) f = 0;
    }
    // Surfaces high above the fire's middle see a little less of it.
    return float4(f.rgb * saturate(1.15 - wp.y * 0.12), f.a);
}

// A flicker for things lit from inside (openings glowing with the fire behind them).
float PezFireFlicker(float3 wp, float t)
{
    return 0.7 + 0.3 * sin(t * 9.1 + wp.x * 3.7 + wp.z * 2.3) * sin(t * 5.3 + wp.y * 4.1 + wp.x);
}
#endif
