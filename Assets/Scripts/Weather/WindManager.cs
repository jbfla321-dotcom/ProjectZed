using System;
using System.Collections.Generic;
using UnityEngine;

// The world's wind: one component (on the "Weather" object) that the weather system drives.
//
// - Trees, grass, undergrowth and clothing move on the GPU (shader ProjectZed/WindLit, Assets/Shaders/Wind). This
//   component only sets three global shader vectors per frame, so the cost doesn't grow with the number of plants.
// - A WindZone is kept in sync for particle systems (rain, leaves, smoke: tick "External Forces").
// - Cloth components (real simulated fabric, if any garment uses one) get the wind as an external acceleration,
//   updated a few times a second and only near the camera.
// - Gameplay can ask GetWindAt(position) for the local wind vector (gusts included), e.g. for particles, sound,
//   bullet drift or fire spread.
//
// Weather hook-up: call SetWind(state, seconds) (or one of the presets) and the wind blends there smoothly.
// Direction / strength / gustiness / turbulence are the whole interface; nothing else needs touching.
[ExecuteAlways]
[DefaultExecutionOrder(-50)] // before anything that reads the wind this frame
public class WindManager : MonoBehaviour
{
    [Serializable]
    public struct WindState
    {
        [Tooltip("Compass direction the wind blows TOWARDS, degrees (0 = +Z / north, 90 = +X / east).")]
        public float direction;
        [Range(0f, 1f), Tooltip("0 calm, 0.2 breeze, 0.5 windy, 1 storm.")]
        public float strength;
        [Range(0f, 1f), Tooltip("How much the wind comes in gusts rather than steadily.")]
        public float gustiness;
        [Range(0f, 1f), Tooltip("How chaotic it is: side-to-side movement and extra leaf flutter.")]
        public float turbulence;

        public WindState(float direction, float strength, float gustiness, float turbulence)
        {
            this.direction = direction; this.strength = strength; this.gustiness = gustiness; this.turbulence = turbulence;
        }

        public static WindState Lerp(WindState a, WindState b, float t) => new WindState(
            a.direction + Mathf.DeltaAngle(a.direction, b.direction) * t,
            Mathf.Lerp(a.strength, b.strength, t), Mathf.Lerp(a.gustiness, b.gustiness, t), Mathf.Lerp(a.turbulence, b.turbulence, t));
    }

    public static readonly WindState Calm = new WindState(60f, 0.03f, 0.2f, 0.1f);
    public static readonly WindState Breeze = new WindState(60f, 0.22f, 0.4f, 0.25f);
    public static readonly WindState Windy = new WindState(60f, 0.5f, 0.6f, 0.45f);
    public static readonly WindState Storm = new WindState(60f, 1f, 0.85f, 0.75f);

    public static WindManager Instance { get; private set; }

    [Tooltip("The wind right now (edit this in the Inspector to try settings; SetWind blends it at runtime).")]
    public WindState current = Breeze;

    [Header("Look")]
    [Tooltip("Wind speed in m/s at strength 1, for GetWindAt and the WindZone.")]
    public float maxSpeed = 25f;
    [Tooltip("Spacing of gust waves rolling across the landscape (metres).")]
    public float gustWavelength = 70f;
    [Tooltip("Leaf / fabric flutter fades out from this distance (shimmering alpha cards far away just look noisy).")]
    public float flutterFadeStart = 45f;
    public float flutterFadeLength = 40f;

    [Header("Vegetation")]
    [Tooltip("Undergrowth (terrain details) grows in from this fraction of the terrain's detail distance, so it doesn't pop in.")]
    [Range(0.3f, 0.95f)] public float detailFadeStart = 0.72f;

    [Header("Cloth components")]
    public float clothUpdateInterval = 0.2f;
    public float clothMaxDistance = 40f;

    public Vector3 Direction { get; private set; } = Vector3.forward;
    public float Strength => current.strength;
    public float Speed => current.strength * maxSpeed;
    /// <summary>Current gust factor (0..1), shared by everything.</summary>
    public float Gust { get; private set; }
    public event Action<WindState> Changed;

    WindState from, to;
    float blendTime, blendDuration;
    double swayPhase, flutterPhase;
    float nextClothUpdate;
    WindZone zone;
    static readonly List<Cloth> cloths = new List<Cloth>();

    static readonly int DirectionId = Shader.PropertyToID("_PZ_WindDirection");
    static readonly int ParamsId = Shader.PropertyToID("_PZ_WindParams");
    static readonly int WaveId = Shader.PropertyToID("_PZ_WindWave");
    static readonly int DetailFadeId = Shader.PropertyToID("_PZ_DetailFade");

    // ---------- Weather API ----------
    /// <summary>Blend to a new wind over `seconds` (0 = at once).</summary>
    public void SetWind(WindState target, float seconds = 0f)
    {
        if (seconds <= 0f || !Application.isPlaying) { current = target; blendDuration = 0f; Changed?.Invoke(current); return; }
        from = current;
        to = target;
        blendTime = 0f;
        blendDuration = seconds;
    }

    public void SetWind(float direction, float strength, float gustiness, float turbulence, float seconds = 0f) =>
        SetWind(new WindState(direction, strength, gustiness, turbulence), seconds);

    public bool IsBlending => blendDuration > 0f;

    /// <summary>The wind at a point (m/s, world space), with gusts. Matches what the trees there are doing.</summary>
    public Vector3 GetWindAt(Vector3 position)
    {
        float wave = Mathf.Sin(Vector2.Dot(new Vector2(position.x, position.z), new Vector2(Direction.x, Direction.z)) * WaveFrequency - (float)swayPhase * 0.7f) * 0.5f + 0.5f;
        float gust = Gust * (0.55f + 0.45f * wave);
        return Direction * (Speed * (0.6f + 0.6f * gust));
    }

    /// <summary>Simulated garments (Cloth components) register here to be blown about.</summary>
    public static void Register(Cloth cloth) { if (!cloths.Contains(cloth)) cloths.Add(cloth); }
    public static void Unregister(Cloth cloth) => cloths.Remove(cloth);

    float WaveFrequency => Mathf.PI * 2f / Mathf.Max(gustWavelength, 1f);

    // ---------- Update ----------
    void OnEnable()
    {
        Instance = this;
        zone = GetComponent<WindZone>();
        if (Application.isPlaying)
            foreach (var c in FindObjectsByType<Cloth>()) Register(c);
        Push(0f);
    }

    void OnDisable()
    {
        if (Instance == this) Instance = null;
        Shader.SetGlobalVector(DirectionId, Vector4.zero); // no wind rather than a frozen one
    }

    void Update()
    {
        float dt = Application.isPlaying ? Time.deltaTime : 1f / 60f;
        if (blendDuration > 0f)
        {
            blendTime += dt;
            float t = Mathf.Clamp01(blendTime / blendDuration);
            current = WindState.Lerp(from, to, Mathf.SmoothStep(0f, 1f, t));
            if (t >= 1f) blendDuration = 0f;
            Changed?.Invoke(current);
        }
        Push(dt);
        if (Application.isPlaying && Time.time >= nextClothUpdate) { nextClothUpdate = Time.time + clothUpdateInterval; UpdateCloth(); }
    }

    void Push(float dt)
    {
        float rad = current.direction * Mathf.Deg2Rad;
        Direction = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        float s = Mathf.Clamp01(current.strength);

        // Gusts: two octaves of slow noise, sharpened and scaled by gustiness.
        float time = Application.isPlaying ? Time.time : (float)(Time.realtimeSinceStartupAsDouble % 10000.0);
        float n = Mathf.PerlinNoise(time * 0.23f, 0.37f) * 0.7f + Mathf.PerlinNoise(time * 0.91f, 7.3f) * 0.3f;
        Gust = Mathf.Clamp01(Mathf.Lerp(0.5f, Mathf.Pow(n, 2.2f - current.gustiness) * 1.6f, current.gustiness));

        // Phases accumulate (faster in stronger wind) so a change of speed never makes anything jump.
        swayPhase += dt * (0.6f + 1.6f * s);
        flutterPhase += dt * (0.8f + 2.4f * s);

        Shader.SetGlobalVector(DirectionId, new Vector4(Direction.x, 0f, Direction.z, s));
        Shader.SetGlobalVector(ParamsId, new Vector4(Gust, current.turbulence, (float)swayPhase, (float)flutterPhase));
        Shader.SetGlobalVector(WaveId, new Vector4(WaveFrequency, flutterFadeStart, flutterFadeLength, 0f));
        var terrain = Terrain.activeTerrain;
        float detailDistance = terrain != null ? terrain.detailObjectDistance : 0f;
        Shader.SetGlobalVector(DetailFadeId, new Vector4(detailDistance * detailFadeStart, detailDistance * 0.97f, 0f, 0f));

        // Only touch the WindZone when the wind actually changed (writing it every frame would keep the scene dirty
        // in the editor).
        if (zone != null && !current.Equals(zoneState))
        {
            zoneState = current;
            zone.mode = WindZoneMode.Directional;
            transform.rotation = Quaternion.LookRotation(Direction);
            zone.windMain = s * 2f;
            zone.windTurbulence = current.turbulence * s * 2f;
            zone.windPulseMagnitude = current.gustiness;
            zone.windPulseFrequency = 0.1f + 0.4f * current.gustiness;
        }
    }

    WindState zoneState = new WindState(float.NaN, 0f, 0f, 0f);

    void UpdateCloth()
    {
        if (cloths.Count == 0) return;
        var cam = Camera.main;
        Vector3 eye = cam != null ? cam.transform.position : Vector3.zero;
        for (int i = cloths.Count - 1; i >= 0; i--)
        {
            var c = cloths[i];
            if (c == null) { cloths.RemoveAt(i); continue; }
            bool near = (c.transform.position - eye).sqrMagnitude < clothMaxDistance * clothMaxDistance;
            Vector3 wind = near ? GetWindAt(c.transform.position) : Vector3.zero;
            c.externalAcceleration = wind * 0.5f;
            c.randomAcceleration = near ? wind * (0.3f + current.turbulence) : Vector3.zero;
        }
    }
}
