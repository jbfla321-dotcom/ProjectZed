# ProjectZed weather and lighting: research and build plan

Research done 2026-10-22 for the weather system: sun through windows, volumetric fog and light shafts, volumetric
clouds, rain, fog and wet surfaces, plus the system that drives them. Engine: Unity 6000.6, URP 17.6 (Render Graph).

## What URP gives us out of the box (and what it doesn't)

- URP has **no built-in volumetric fog or clouds**. Those are HDRP features (HDRP has the physically based sky and
  volumetric clouds); URP needs custom render passes or packages.
- URP 17 **does** have Adaptive Probe Volumes (APV) with **sky occlusion**: a baked per-probe "how much sky reaches
  here" value, combined at runtime with the current sky colour. That's the key to interiors: rooms get darker than
  outdoors, and the indoor ambient still follows the weather (overcast vs clear) without rebaking. It needs the GPU
  lightmapper and costs bake time and some memory.
- Sun shadows through windows already work in principle: the panes don't cast shadows. Sun shadow distance, cascades
  and softness need tuning so frames and furniture throw crisp shadows indoors.

## Off-the-shelf pieces worth using (all MIT, free)

| What | Project | Fit |
|---|---|---|
| Volumetric fog + light shafts | [CristianQiu/Unity-URP-Volumetric-Light](https://github.com/CristianQiu/Unity-URP-Volumetric-Light) | MIT. Supports up to Unity 6000.6 with Render Graph. Main light with shadows (gives beams through windows and trees), point/spot lights, APV, URP Volume integration, Forward+. Known limits: fog only to a set distance, transparent objects don't blend with it (glass panes), some noise at angles. **Recommended.** |
| Volumetric clouds | [stencg/UnityVolumetricCloudsURP](https://github.com/stencg/UnityVolumetricCloudsURP) (fork of jiaozi158's, ported from HDRP's clouds) | MIT. Unity 6 + URP 17.0.4+, Render Graph only. Cloud shadows baked into the sun's light cookie, low-res denoise option, static/dynamic ambient. Wind and coverage controls aren't documented and custom cloud maps are "work in progress", so the weather hook needs checking. **Try first; write our own if it doesn't fit.** |
| Fog alternative | [sinnwrig/URP-Fog-Volumes](https://github.com/sinnwrig/URP-Fog-Volumes) | MIT, local fog volumes, up to 32 lights; no self-shadowing. Backup only. |

If the cloud package doesn't fit, the reference for building our own is Guerrilla's Nubis
(Horizon Zero Dawn, [SIGGRAPH 2015](https://www.guerrilla-games.com/read/the-real-time-volumetric-cloudscapes-of-horizon-zero-dawn),
[Nubis 2017](https://www.guerrilla-games.com/read/nubis-authoring-real-time-volumetric-cloudscapes-with-the-decima-engine)):
2.5D cloud maps plus 3D noise detail, raymarched at low resolution with temporal reprojection, about 2 ms on a PS4.

## Rain and wet surfaces

- **Rain occlusion** (no rain inside houses or under porches): render a small top-down depth map of roofs around
  the player and kill drops below it. Standard approach in shipped games and commercial rain kits
  ([bugnet guide](https://bugnet.io/blog/how-to-fix-rain-particles-falling-through-roofs-and-overhangs),
  [Unreal forum](https://forums.unrealengine.com/t/gpu-particle-collision-only-working-partially/282523),
  [Fab rain kit](https://www.fab.com/listings/0258774d-3d7b-4b1a-8b33-1633ba5628d8)). The same map tells
  surfaces whether they're under cover, so dry porches stay dry.
- **Rain itself**: GPU-instanced streaks in a box that follows the camera, slanted by the wind
  (`WindManager.GetWindAt`). Splashes where drops would land (from the depth map), and ripples on puddles.
- **Wet surfaces** (Sébastien Lagarde, [Water drop 2b](https://seblagarde.wordpress.com/2013/01/03/water-drop-2b-dynamic-rain-and-its-effects/)):
  one 0–1 wetness level. Darken albedo (down to about 0.3x at full wet on porous surfaces), raise smoothness (up to
  2.5x, clamped), keep the normal. Puddles: smoothness to 1, flat normal, water F0 ~0.02. Small cracks fill and dry
  faster than puddles. His budget: about 3 ms at 30 fps for core rain. For us this means a wetness global read by our
  own shaders (WindLit already is one; houses and terrain would need wet-aware variants).

## How the weather is driven (DayZ / Arma model)

DayZ and Arma use a few 0–1 values that drift toward targets over minutes
([DayZ cfgweather guide](https://xgamingserver.com/docs/dayz/weather), [Arma setOvercast](https://community.bistudio.com/wiki/setOvercast)):
- **Overcast** is the master value: clouds, sun strength, and wind all follow it.
- **Rain** can only start inside an overcast range, and stops after a delay when overcast leaves it.
- **Fog** runs independently.
- **Storms**: lightning when overcast is above a threshold, with a set density and time between strikes.
- Each value has limits, a change time (DayZ uses 10–30 min) and a hold time before the next change.

Our version: a `WeatherManager` with these values and presets (Clear, Overcast, Foggy, Rain, Storm), blending over
minutes, driving WindManager, clouds, fog, sun, rain, wetness and ambience audio. Thunder gets its distance delay for
free from SoundManager (it already delays sounds by distance / 343 m/s).

## Build plan (pending "lets go")

1. **Sun through windows**: tune the sun's shadows; bake APV with sky occlusion over the world and houses; a
   reflection probe per room. Check interiors in screenshots.
2. **Volumetric fog + light shafts**: install CristianQiu's package, replace the current exp² fog with it, tune
   density and distance for the PNW look.
3. **Clouds**: try the stencg package; hook coverage, wind and cloud shadows to the weather. Write our own if it
   can't be driven.
4. **WeatherManager**: overcast / fog / rain / storm values with presets and timed blends; dev console `weather`
   commands.
5. **Rain**: camera-following GPU rain, top-down occlusion map, splashes, wetness on our shaders.
6. **Audio**: rain loops (outdoor, indoor-on-roof), thunder, wind gusts. Needs Sonniss downloads, listed for OK first.

Performance: the field edge already costs ~30 ms CPU in the editor. Fog, clouds and rain mostly cost GPU; each gets
quality settings (resolution scale, step counts, rain density).

## Open questions

- Day/night cycle now or later? (Everything above works with a moving sun.)
- Lightning flashes in storms?
- OK to install the two MIT packages (fog, clouds) from GitHub?
