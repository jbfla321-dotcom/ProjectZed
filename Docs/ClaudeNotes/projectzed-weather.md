---
name: projectzed-weather
description: "ProjectZed weather/day-night (2026-10-22) - packages, scripts, setup tool, calibration gotchas, what's left"
metadata:
  node_type: memory
  type: project
  modified: 2026-10-22T21:07:08.973Z
---

Started 2026-10-22 for [[projectzed-shooter]]. User wants: day/night now, volumetric clouds, rain, fog, storms, sun shadows through windows, a weather menu to set weather any time; power stays out (generators later).

- Packages (MIT, pinned commits in manifest): com.cqf.urpvolumetricfog (CristianQiu), com.jiaozi158.unity-volumetric-clouds-urp + unity-physically-based-sky-urp (stencg forks). Global types, no namespace: VolumetricClouds, PhysicallyBasedSky, Fog, VisualEnvironment, VolumetricFogVolumeComponent.
- Scripts (Assets/Scripts/Weather): TimeOfDay (real sun path lat 47.5, sun->moon handover on one light), WeatherManager (overcast/fog/rain/storm/wind presets + blends, DayZ-style auto, drives clouds/sky/fog volume + RenderSettings exp2 fog), RainSystem (GPU streaks + splashes, raycast shelter height map; shaders Assets/Shaders/Weather), Lightning, WetSurfaces (restores materials on disable), WeatherMenu (F7). Console: weather/time.
- Editor: Tools > Environment > Setup Weather (WeatherSetup.cs, idempotent: HDR, 4 cascades 150 m, features, defines VC_SHADOWS;VC_DENOISE;VC_DEPTH;OPTIMISATION, WeatherProfile + Stars cubemap, scene comps), Capture Weather (WeatherCapture.cs, also FogTestFromCommandLine).
- Gotchas: PB sky colours the sky by the main light colour (keep sun white; it reddens it itself). Sky Fog(URP) override showed no effect -> disabled; distance haze = RenderSettings exp2 (trees stop at 175 m so clear days keep some haze). Graphics.RenderMesh in editor captures: queue per cam.Render with RenderParams.camera. Batch Unity compiler sometimes crashes (CLR 0x80131506): just rerun.
- Interiors: Tools > Environment > Bake House Lighting (HouseLightingBake.cs; APV + sky occlusion, local volume per site, ~25 s; rerun after Build Houses). WeatherManager scales APV intensity by daylight/overcast (else baked day sky lit rooms at night); EyeAdaptation.cs raises exposure indoors (+3 stops) / at night. Volumetric fog APV contribution must stay off (glowed at night). House is [ExecuteAlways] so House.At works in edit-mode captures.
- Rain/thunder audio: Ambience/Rain_*_Loop (slice.py `loop` mode), Thunder event; RainAudio.cs crossfades outdoor/indoor beds.
- Not done yet (2026-10-22): hand play-test in Play mode, perf measurement, player flashlight (nights indoors are pitch dark).
