using UnityEngine;

// The clock and the sky's lights. Lives on the "Weather" object next to WindManager and WeatherManager.
//
// Moves the scene's directional light (RenderSettings.sun, which the physically based sky also reads) along the real
// sun path for the latitude and day of year, colours it by altitude, and at night turns the same light into the moon
// (URP has one shadowed main light, so the sun hands over to the moon below the horizon). WeatherManager dims it
// for clouds through cloudDimmer. Stars come from the sky's space emission (WeatherManager sets them).
[ExecuteAlways]
[DefaultExecutionOrder(-40)]
public class TimeOfDay : MonoBehaviour
{
    public static TimeOfDay Instance { get; private set; }

    [Header("Clock")]
    [Range(0f, 24f), Tooltip("Hour of the day, 0-24.")]
    public float time = 10.5f;
    [Tooltip("Real minutes per in-game day (0 = the clock stands still).")]
    public float dayLengthMinutes = 120f;
    [Range(1, 365), Tooltip("Day of the year (sun height and day length follow the season).")]
    public int dayOfYear = 270;
    [Tooltip("Run the clock in Edit mode too.")]
    public bool runClockInEditMode;

    [Header("Place")]
    [Tooltip("Latitude in degrees (Pacific Northwest ~47.5).")]
    public float latitude = 47.5f;
    [Tooltip("Where north is: rotates the whole sky around the vertical (0 = +Z is north).")]
    public float northOffset;

    [Header("Lights")]
    public Light sun;
    [Tooltip("Sun intensity with the sun high and no clouds.")]
    public float sunIntensity = 3f;
    [Tooltip("Sun intensity by altitude (degrees above the horizon, 0-90 mapped to 0-1).")]
    public AnimationCurve sunByAltitude = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.05f, 0.35f), new Keyframe(0.2f, 0.8f), new Keyframe(1f, 1f));
    [Tooltip("Sun colour (the sky reddens it near the horizon itself, through the atmosphere).")]
    public Color sunColor = new Color(1f, 0.98f, 0.95f);
    [Tooltip("Full moon light intensity.")]
    public float moonIntensity = 0.12f;
    public Color moonColor = new Color(0.62f, 0.72f, 1f);
    [Range(0f, 1f), Tooltip("Moon phase: 0 new, 0.5 full, 1 new again.")]
    public float moonPhase = 0.5f;

    [Header("Driven by WeatherManager")]
    [Range(0f, 1f)] public float cloudDimmer = 1f;

    public float SunAltitude { get; private set; }        // degrees
    public Vector3 SunDirection { get; private set; }     // towards the sun
    public Vector3 MoonDirection { get; private set; }
    public bool IsNight => SunAltitude < -4f;
    // 1 in full daylight, 0 at night, fading through dusk/dawn (sun -8..6 degrees).
    public float Daylight => Mathf.Clamp01(Mathf.InverseLerp(-8f, 6f, SunAltitude));

    void OnEnable()
    {
        Instance = this;
        if (sun == null) sun = RenderSettings.sun;
        if (sun == null) foreach (var l in FindObjectsByType<Light>()) if (l.type == LightType.Directional) { sun = l; break; }
        if (sun != null) RenderSettings.sun = sun;
        Apply();
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
    }

    public void SetTime(float hour) { time = Mathf.Repeat(hour, 24f); Apply(); }

    void Update()
    {
        if ((Application.isPlaying || runClockInEditMode) && dayLengthMinutes > 0f)
            time = Mathf.Repeat(time + Time.deltaTime * 24f / (dayLengthMinutes * 60f), 24f);
        Apply();
    }

    void OnValidate() => Apply();

    void Apply()
    {
        // Solar position (simple model, good to a degree or so): declination from the day of year, hour angle from
        // local solar time.
        float decl = -23.44f * Mathf.Cos(Mathf.Deg2Rad * 360f / 365f * (dayOfYear + 10));
        SunDirection = SkyDirection(15f * (time - 12f), decl, out float alt);
        SunAltitude = alt;
        // Moon: opposite the sun at full, alongside it at new; on roughly the opposite declination.
        MoonDirection = SkyDirection(15f * (time - 12f) + 180f + (moonPhase - 0.5f) * 360f, -decl, out float moonAlt);

        if (sun == null) return;
        Vector3 toLight;
        float intensity;
        Color color;
        if (SunAltitude > -6f)
        {
            toLight = SunDirection;
            float a = Mathf.Clamp01(SunAltitude / 90f);
            intensity = sunIntensity * sunByAltitude.Evaluate(a) * Mathf.Clamp01(Mathf.InverseLerp(-6f, 1f, SunAltitude));
            color = sunColor;
        }
        else
        {
            toLight = MoonDirection;
            float full = 1f - Mathf.Abs(moonPhase - 0.5f) * 2f;
            float up = Mathf.Clamp01(Mathf.InverseLerp(-2f, 10f, moonAlt));
            // Fade in as the sun sinks further, so there's no jump at the handover.
            intensity = moonIntensity * Mathf.Lerp(0.15f, 1f, full) * up * Mathf.Clamp01(Mathf.InverseLerp(-6f, -12f, SunAltitude));
            color = moonColor;
        }
        sun.transform.rotation = Quaternion.LookRotation(-toLight, Vector3.up);
        sun.intensity = intensity * cloudDimmer;
        sun.color = color;
        sun.useColorTemperature = false;
        sun.shadows = intensity > 0.001f ? LightShadows.Soft : LightShadows.None;
    }

    // Direction towards a body at this hour angle (degrees, 0 = due south at noon) and declination.
    Vector3 SkyDirection(float hourAngle, float declination, out float altitude)
    {
        float lat = latitude * Mathf.Deg2Rad, h = hourAngle * Mathf.Deg2Rad, d = declination * Mathf.Deg2Rad;
        float sinAlt = Mathf.Sin(lat) * Mathf.Sin(d) + Mathf.Cos(lat) * Mathf.Cos(d) * Mathf.Cos(h);
        float alt = Mathf.Asin(Mathf.Clamp(sinAlt, -1f, 1f));
        // Azimuth from north towards east.
        float az = Mathf.Atan2(Mathf.Sin(h), Mathf.Cos(h) * Mathf.Sin(lat) - Mathf.Tan(d) * Mathf.Cos(lat)) + Mathf.PI;
        az += northOffset * Mathf.Deg2Rad;
        altitude = alt * Mathf.Rad2Deg;
        return new Vector3(Mathf.Sin(az) * Mathf.Cos(alt), Mathf.Sin(alt), Mathf.Cos(az) * Mathf.Cos(alt));
    }
}
