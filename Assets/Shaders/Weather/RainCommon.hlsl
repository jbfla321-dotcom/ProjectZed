#ifndef PZ_RAIN_COMMON_INCLUDED
#define PZ_RAIN_COMMON_INCLUDED

// Set per frame by RainSystem.
float4 _RainBoxSize;       // xyz box size (m), w intensity 0-1
float4 _RainCenter;        // xyz camera position, w time (s)
float4 _RainVelocity;      // xyz drop velocity (m/s), w streak length (m)
float4 _RainParams;        // x streak width (m), y splash rate (per s)
float4 _RainShelterRect;   // xy world xz of the map's corner, z 1 / size
TEXTURE2D(_RainShelter);
SAMPLER(sampler_RainShelter);

// Height of the highest surface at this spot (roof, tree, ground); very low outside the mapped area.
float RainShelterHeight(float2 xz)
{
    float2 uv = (xz - _RainShelterRect.xy) * _RainShelterRect.z;
    if (any(uv < 0.0) || any(uv > 1.0)) return -1e5;
    return SAMPLE_TEXTURE2D_LOD(_RainShelter, sampler_RainShelter, uv, 0).r;
}

#endif
