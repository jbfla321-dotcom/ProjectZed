#ifndef PZ_WIND_INCLUDED
#define PZ_WIND_INCLUDED

// ProjectZed wind: vertex displacement for trees, plants and clothing, all on the GPU.
// The only per-frame CPU cost is WindManager setting the three globals below (Shader.SetGlobalVector).
//
//   _PZ_WindDirection  xyz world direction the wind blows towards (unit, y = 0), w strength (0 calm .. 1 storm)
//   _PZ_WindParams     x gust (0..1, current), y turbulence (0..1), z sway phase, w flutter phase
//                      (phases are accumulated on the CPU, so changing the wind speed never makes things jump)
//   _PZ_WindWave       x gust-wave spatial frequency (1/m), y flutter fade start (m), z flutter fade length (m), w unused
//   _PZ_DetailFade     x start, y end (m): _WIND_PLANT meshes (terrain details) shrink to nothing between the two, so
//                      undergrowth grows in with distance instead of popping in where the terrain starts drawing it
//
// Per material (UnityPerMaterial, see WindLitInput.hlsl): _WindParams / _WindParams2.
// Modes (one keyword): _WIND_TREE (bend with height, flutter away from the trunk), _WIND_PLANT (bend with height,
// for grass, ferns, shrubs), _WIND_CLOTH (outward-only ripple, so fabric never sinks into the body).

float4 _PZ_WindDirection;
float4 _PZ_WindParams;
float4 _PZ_WindWave;
float4 _PZ_DetailFade;

#if defined(_WIND_TREE) || defined(_WIND_PLANT) || defined(_WIND_CLOTH)
    #define PZ_WIND_ON
#endif

float3 PZ_ApplyWind(float3 positionOS, float3 normalOS)
{
#if defined(PZ_WIND_ON)
    float3 positionWS = TransformObjectToWorld(positionOS);
    float3 originWS = GetObjectToWorldMatrix()._m03_m13_m23;
  #if defined(_WIND_PLANT)
    if (_PZ_DetailFade.y > 0.0)
    {
        float grow = saturate((_PZ_DetailFade.y - distance(originWS, GetCameraPositionWS())) / max(_PZ_DetailFade.y - _PZ_DetailFade.x, 1.0));
        positionWS = originWS + (positionWS - originWS) * grow;
    }
  #endif

    float strength = _PZ_WindDirection.w;
    if (strength <= 0.001) return TransformWorldToObject(positionWS);

    float3 dir = _PZ_WindDirection.xyz;
    float gust = _PZ_WindParams.x, turbulence = _PZ_WindParams.y;
    float swayPhase = _PZ_WindParams.z, flutterPhase = _PZ_WindParams.w;
    float3 normalWS = TransformObjectToWorldNormal(normalOS);
    float scaleY = length(GetObjectToWorldMatrix()._m01_m11_m21);

    // Gusts roll across the landscape as waves along the wind, so neighbouring trees move together.
    float along = dot(originWS.xz, dir.xz) * _PZ_WindWave.x;
    float wave = sin(along - swayPhase * 0.7) * 0.5 + 0.5;
    float gustHere = saturate(gust * (0.55 + 0.45 * wave) * (1.0 - _WindParams2.z * 0.5));
    float objectPhase = dot(originWS.xz, float2(0.173, 0.091)); // each tree / plant out of step with the next

    float distanceToCamera = length(positionWS - GetCameraPositionWS());
    float flutterFade = saturate(1.0 - (distanceToCamera - _PZ_WindWave.y) / max(_PZ_WindWave.z, 1.0));
    float3 offset = 0;

  #if defined(_WIND_CLOTH)
    // Fabric: ripples pushed outward along the normal (never into the body), strongest on the leeward side where
    // cloth flaps, plus a little drift downwind.
    float ripple = sin(dot(positionWS, float3(7.1, 5.3, 6.7)) + flutterPhase * _WindParams2.y + objectPhase) * 0.5 + 0.5;
    float lee = 0.6 + 0.4 * saturate(dot(normalWS, dir));
    float amount = strength * (0.4 + 0.6 * gustHere) * lee * flutterFade;
    offset = normalWS * (_WindParams.z * ripple * amount) + dir * (_WindParams.y * amount);
  #else
    float height = max(_WindParams.x * scaleY, 0.01);
    float t = saturate((positionWS.y - originWS.y) / height);
    float bend = t * t;

    // Sway: a lean downwind (grows with strength and gusts) plus an oscillation about it.
    float lean = strength * (0.35 + 0.65 * gustHere);
    float osc = sin(swayPhase * _WindParams2.x + objectPhase) * (0.25 + 0.5 * turbulence) * (0.3 + 0.7 * strength);
    float3 side = float3(-dir.z, 0, dir.x);
    float sideOsc = sin(swayPhase * _WindParams2.x * 1.37 + objectPhase * 1.9) * 0.3 * turbulence * strength;
    float3 sway = (dir * (lean + osc) + side * sideOsc) * (_WindParams.y * scaleY * bend);
    // Keep the length roughly constant: what moves sideways comes down a little.
    sway.y -= dot(sway.xz, sway.xz) * 0.5 / height;
    offset = sway;

    // Flutter: leaves and fronds shiver, more toward the branch tips (tree) or the top (plant).
    #if defined(_WIND_TREE)
        float flutterWeight = saturate(length(positionWS.xz - originWS.xz) / max(_WindParams.w * scaleY, 0.01)) * saturate(t * 3.0);
    #else
        float flutterWeight = t;
    #endif
    float shiver = sin(flutterPhase * _WindParams2.y + dot(positionWS, float3(1.7, 2.3, 1.3)))
                 * sin(flutterPhase * _WindParams2.y * 0.61 + dot(positionWS, float3(0.9, 1.1, 2.1)));
    float flutter = _WindParams.z * flutterWeight * shiver * (0.2 + 0.8 * strength) * (0.5 + 0.5 * gustHere + turbulence * 0.5) * flutterFade;
    // Not along the normal: the depth-only pass has no normals, and every pass must move vertices identically.
    offset += float3(dir.x * 0.5, 0.85, dir.z * 0.5) * flutter;
  #endif

    return TransformWorldToObject(positionWS + offset);
#else
    return positionOS;
#endif
}

#endif
