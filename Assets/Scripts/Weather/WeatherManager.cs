using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// The weather: a handful of 0-1 values (DayZ / Arma style) that blend toward targets and drive everything else.
// Lives on the "Weather" object with WindManager, TimeOfDay and RainSystem.
//
//   overcast  master value: cloud cover, sun strength, greyer sky, wind follows it in Auto mode
//   fog       ground fog and haze (independent)
//   rain      rain amount; Auto mode only rains when overcast is high enough
//   storm     lightning and thunder (needs rain)
//   wind      wind strength, handed to WindManager (direction stays WindManager's)
//
// Set it from anywhere: WeatherManager.Instance.SetPreset("Rain", 30f) or Set(state, seconds). The weather menu
// (F7), the dev console ("weather ...") and the inspector buttons all go through these.
[ExecuteAlways]
[DefaultExecutionOrder(-45)] // before WindManager (-50 runs first, then us; we hand it a target only on changes)
public class WeatherManager : MonoBehaviour
{
    [Serializable]
    public struct WeatherState
    {
        [Range(0f, 1f)] public float overcast;
        [Range(0f, 1f)] public float fog;
        [Range(0f, 1f)] public float rain;
        [Range(0f, 1f)] public float storm;
        [Range(0f, 1f)] public float wind;
        public WeatherState(float overcast, float fog, float rain, float storm, float wind)
        {
            this.overcast = overcast; this.fog = fog; this.rain = rain; this.storm = storm; this.wind = wind;
        }
        public static WeatherState Lerp(WeatherState a, WeatherState b, float t) => new WeatherState(
            Mathf.Lerp(a.overcast, b.overcast, t), Mathf.Lerp(a.fog, b.fog, t), Mathf.Lerp(a.rain, b.rain, t),
            Mathf.Lerp(a.storm, b.storm, t), Mathf.Lerp(a.wind, b.wind, t));
        public override string ToString() => $"overcast {overcast:F2}, fog {fog:F2}, rain {rain:F2}, storm {storm:F2}, wind {wind:F2}";
    }

    public static readonly (string name, WeatherState state)[] Presets =
    {
        ("Sunny", new WeatherState(0.05f, 0.04f, 0f, 0f, 0.12f)),
        ("Cloudy", new WeatherState(0.5f, 0.1f, 0f, 0f, 0.25f)),
        ("Overcast", new WeatherState(0.82f, 0.25f, 0f, 0f, 0.3f)),
        ("Foggy", new WeatherState(0.65f, 0.9f, 0f, 0f, 0.05f)),
        ("Drizzle", new WeatherState(0.85f, 0.4f, 0.25f, 0f, 0.25f)),
        ("Rain", new WeatherState(0.92f, 0.35f, 0.65f, 0f, 0.45f)),
        ("Storm", new WeatherState(1f, 0.3f, 1f, 1f, 0.95f)),
    };

    public static WeatherManager Instance { get; private set; }

    [Tooltip("The weather right now (blends toward the target).")]
    public WeatherState current = new WeatherState(0.82f, 0.25f, 0f, 0f, 0.3f);
    [Tooltip("Where it's heading.")]
    public WeatherState target = new WeatherState(0.82f, 0.25f, 0f, 0f, 0.3f);

    [Header("Auto weather")]
    [Tooltip("Let the weather change on its own (off while you set it by hand).")]
    public bool autoWeather = true;
    [Tooltip("Each auto change takes this many in-game... real minutes (random between x and y).")]
    public Vector2 autoChangeMinutes = new Vector2(8f, 20f);
    [Tooltip("And then holds for this many real minutes.")]
    public Vector2 autoHoldMinutes = new Vector2(10f, 30f);
    [Tooltip("Rain only starts in Auto mode when overcast is above this.")]
    public float rainOvercastThreshold = 0.75f;

    [Header("Scene")]
    [Tooltip("The global Volume holding the sky, fog and cloud overrides.")]
    public Volume volume;
    public RainSystem rain;
    public Lightning lightning;
    public WetSurfaces wetSurfaces;
    [Tooltip("Distance haze (exp² fog density) at fog 0 and 1. 0.0067 leaves a quarter of the view at 175 m.")]
    public Vector2 hazeDensity = new Vector2(0.0065f, 0.04f);
    [Tooltip("Sky brightness (exposure) by day and by night.")]
    public float dayExposure = 1f, nightExposure = 1.2f;
    public float starBrightness = 0.35f;
    [Header("Cloud quality")]
    public bool microErosion = true;
    [Range(12, 128)] public int cloudSteps = 64;
    [Range(1, 16)] public int cloudLightSteps = 6;
    public float cloudDensityScale = 1f;

    public bool IsBlending => blendTime < blendDuration;
    public event Action<WeatherState> Changed;

    WeatherState from;
    float blendTime, blendDuration;
    float autoNext;
    float lastWind = -1f;
    VolumeProfile profile;

    void OnEnable()
    {
        Instance = this;
        from = current;
        blendTime = blendDuration = 0f;
        if (rain == null) rain = GetComponent<RainSystem>();
        if (lightning == null) lightning = GetComponent<Lightning>();
        if (wetSurfaces == null) wetSurfaces = GetComponent<WetSurfaces>();
        profile = null;
        Apply();
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
    }

    // ---------- Control ----------
    public void Set(WeatherState state, float seconds = 0f)
    {
        from = current;
        target = state;
        blendDuration = Mathf.Max(0f, seconds);
        blendTime = 0f;
        if (blendDuration <= 0f) { current = state; Apply(); }
        Changed?.Invoke(target);
    }

    public bool SetPreset(string name, float seconds = 0f)
    {
        foreach (var p in Presets)
            if (string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase)) { Set(p.state, seconds); return true; }
        return false;
    }

    // ---------- Update ----------
    void Update()
    {
        float dt = Application.isPlaying ? Time.deltaTime : 0f;
        if (blendTime < blendDuration)
        {
            blendTime += dt;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(blendTime / blendDuration));
            current = WeatherState.Lerp(from, target, t);
        }
        else if (!Application.isPlaying)
        {
            current = target;   // edit mode: show the target straight away
        }
        if (Application.isPlaying && autoWeather && !IsBlending && Time.time >= autoNext) AutoChange();
        Apply();
    }

    // DayZ-like: pick a new overcast and fog, rain only if it's cloudy enough, storms rarely.
    void AutoChange()
    {
        if (autoNext == 0f) { autoNext = Time.time + UnityEngine.Random.Range(autoHoldMinutes.x, autoHoldMinutes.y) * 60f; return; }
        var s = new WeatherState
        {
            overcast = Mathf.Clamp01(current.overcast + UnityEngine.Random.Range(-0.45f, 0.45f)),
            fog = Mathf.Clamp01(UnityEngine.Random.value < 0.25f ? UnityEngine.Random.Range(0.5f, 0.9f) : UnityEngine.Random.Range(0.05f, 0.35f)),
        };
        s.rain = s.overcast > rainOvercastThreshold ? Mathf.InverseLerp(rainOvercastThreshold, 1f, s.overcast) * UnityEngine.Random.Range(0.3f, 1f) : 0f;
        s.storm = s.rain > 0.6f && UnityEngine.Random.value < 0.3f ? UnityEngine.Random.Range(0.5f, 1f) : 0f;
        s.wind = Mathf.Clamp01(0.08f + s.overcast * 0.4f + s.storm * 0.5f + UnityEngine.Random.Range(-0.1f, 0.1f));
        float change = UnityEngine.Random.Range(autoChangeMinutes.x, autoChangeMinutes.y) * 60f;
        Set(s, change);
        autoNext = Time.time + change + UnityEngine.Random.Range(autoHoldMinutes.x, autoHoldMinutes.y) * 60f;
    }

    // ---------- Apply to the world ----------
    public void ApplyNow() => Apply();

    void Apply()
    {
        var w = current;
        var tod = TimeOfDay.Instance;
        if (tod != null) tod.cloudDimmer = Mathf.Lerp(1f, 0.3f, Mathf.Pow(w.overcast, 1.6f)) * Mathf.Lerp(1f, 0.75f, w.fog);

        var wind = WindManager.Instance;
        if (wind != null && Mathf.Abs(w.wind - lastWind) > 0.005f)
        {
            lastWind = w.wind;
            var ws = WindManager.WindState.Lerp(WindManager.Calm, WindManager.Storm, w.wind);
            ws.direction = wind.current.direction;
            ws.strength = w.wind;
            wind.SetWind(ws, 0f);
        }

        ApplyVolume(w, tod);
        if (rain != null) rain.intensity = w.rain;
        if (lightning != null) lightning.storm = w.rain > 0.2f ? w.storm : 0f;
        if (wetSurfaces != null) wetSurfaces.raining = w.rain;
    }

    void ApplyVolume(WeatherState w, TimeOfDay tod)
    {
        if (volume == null) return;
        if (profile == null) profile = Application.isPlaying ? volume.profile : volume.sharedProfile;
        if (profile == null) return;
        float windDir = WindManager.Instance != null ? WindManager.Instance.current.direction : 0f;
        float windSpeed = WindManager.Instance != null ? WindManager.Instance.Speed : 5f;
        float night = tod != null ? 1f - tod.Daylight : 0f;

        if (profile.TryGet(out VolumetricClouds clouds))
        {
            float o = w.overcast;
            clouds.state.Override(o > 0.02f);
            // Coverage: shape factor falls from "almost nothing" through sparse and cloudy to a closed overcast deck.
            clouds.shapeFactor.Override(Curve(o, (0f, 0.995f), (0.3f, 0.94f), (0.55f, 0.88f), (0.8f, 0.6f), (1f, 0.45f)));
            clouds.densityMultiplier.Override((Curve(o, (0f, 0.45f), (0.5f, 0.55f), (1f, 0.6f)) + w.storm * 0.15f) * cloudDensityScale);
            // Crisp, billowy edges: strong erosion plus the fine micro-erosion layer (softer on an overcast deck)
            clouds.erosionFactor.Override(Curve(o, (0f, 0.9f), (0.55f, 0.9f), (1f, 0.65f)));
            clouds.erosionScale.Override(75f);
            clouds.shapeScale.Override(Curve(o, (0f, 6f), (0.55f, 5f), (1f, 4f)));
            clouds.microErosion.Override(microErosion);
            clouds.microErosionFactor.Override(Curve(o, (0f, 0.65f), (1f, 0.45f)));
            clouds.microErosionScale.Override(300f);
            clouds.numPrimarySteps.Override(cloudSteps);
            clouds.numLightSteps.Override(cloudLightSteps);
            clouds.bottomAltitude.Override(Curve(o, (0f, 2800f), (0.5f, 1300f), (1f, 900f)));
            clouds.altitudeRange.Override(Curve(o, (0f, 1000f), (0.5f, 2000f), (1f, 2500f)) + w.storm * 2000f);
            clouds.globalSpeed.Override(windSpeed * 3.6f * 2f);    // km/h, upper winds a bit faster
            clouds.globalOrientation.Override(Mathf.Repeat(windDir + 180f, 360f));
            clouds.sunLightDimmer.Override(Mathf.Lerp(1f, 0.55f, w.storm));
            clouds.scatteringTint.Override(Color.Lerp(new Color(0.8f, 0.8f, 0.8f), new Color(0.45f, 0.47f, 0.5f), w.storm * 0.8f + w.rain * 0.2f));
            clouds.shadows.Override(true);
            clouds.shadowOpacity.Override(Mathf.Lerp(0.6f, 1f, o));
        }

        if (profile.TryGet(out PhysicallyBasedSky sky))
        {
            // Greyer, flatter sky under cloud (also greys the ambient light the sky hands out)
            sky.colorSaturation.Override(Mathf.Lerp(1f, 0.3f, Mathf.Max(w.overcast * 0.9f, w.fog * 0.7f)));
            sky.alphaSaturation.Override(Mathf.Lerp(1f, 0.5f, w.overcast));
            // A little brighter at night so it's dark, not black
            sky.exposure.Override(Mathf.Lerp(dayExposure, nightExposure, night));
            // Stars only once it's dark, dimmed by cloud and fog
            sky.spaceEmissionMultiplier.Override(starBrightness * night * night * (1f - w.overcast) * (1f - w.fog));
        }

        // The sky package's height fog doesn't show in our setup, so it stays off: distance haze is Unity's own exp²
        // fog (works on every shader, and hides where the terrain stops drawing trees at ~175 m), coloured like the
        // sky at the horizon; the volumetric fog adds the light shafts and the thick stuff near the ground.
        if (profile.TryGet(out Fog skyFog)) skyFog.enabled.Override(false);
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = Mathf.Lerp(hazeDensity.x, hazeDensity.y, Mathf.Pow(Mathf.Max(w.fog, w.rain * 0.45f), 1.3f));
        RenderSettings.fogColor = HorizonColor(w, night);

        // Baked probe fill (indoors mostly) follows the daylight and the cloud cover
        if (profile.TryGet(out ProbeVolumesOptions apv))
            apv.intensityMultiplier.Override(Mathf.Lerp(0.04f, 1f, tod != null ? tod.Daylight : 1f) * Mathf.Lerp(1f, 0.55f, w.overcast));

        if (profile.TryGet(out VolumetricFogVolumeComponent vfog))
        {
            vfog.enabled.Override(true);
            vfog.density.Override(Mathf.Lerp(0.04f, 0.6f, Mathf.Pow(w.fog, 0.8f)) + w.rain * 0.08f);
            vfog.attenuationDistance.Override(Mathf.Lerp(500f, 35f, Mathf.Pow(w.fog, 0.6f)));
            vfog.distance.Override(200f);
            vfog.maximumHeight.Override(Mathf.Lerp(120f, 40f, w.fog));
            vfog.anisotropy.Override(0.55f);
            vfog.scattering.Override(Mathf.Lerp(0.08f, 0.3f, w.fog));
            vfog.enableMainLightContribution.Override(true);
            vfog.enableAPVContribution.Override(false);   // baked probes are static; they made the fog glow at night
        }
    }

    // Fog colour: the sky's ambient light from the horizon, greyed by cloud and dimmed at night.
    Color HorizonColor(WeatherState w, float night)
    {
        var probe = RenderSettings.ambientProbe;
        var dirs = new[] { new Vector3(1f, 0.1f, 0f), new Vector3(-1f, 0.1f, 0f), new Vector3(0f, 0.1f, 1f), new Vector3(0f, 0.1f, -1f) };
        var results = new Color[4];
        probe.Evaluate(dirs, results);
        Color c = (results[0] + results[1] + results[2] + results[3]) * 0.25f;
        float grey = c.grayscale;
        c = Color.Lerp(c, new Color(grey, grey, grey), Mathf.Max(w.overcast, w.fog) * 0.7f);
        c.a = 1f;
        return c;
    }

    static float Curve(float x, params (float x, float y)[] keys)
    {
        if (x <= keys[0].x) return keys[0].y;
        for (int i = 1; i < keys.Length; i++)
            if (x <= keys[i].x) return Mathf.Lerp(keys[i - 1].y, keys[i].y, Mathf.InverseLerp(keys[i - 1].x, keys[i].x, x));
        return keys[keys.Length - 1].y;
    }
}
