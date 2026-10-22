using UnityEngine;
using UnityEngine.Audio;

// Plays SoundEvents from a fixed pool of AudioSources (no allocation per sound).
// 3D sounds arrive late by distance / 343 m/s (so a far shot is seen before it's heard) and are low-passed
// with distance, the way air soaks up high frequencies. Layered events (close/mid/far) play one voice per
// audible layer. Also switches the mixer snapshots: InVehicle while the player drives, Paused while the
// game is paused, Outdoor otherwise (Indoor via SetIndoor).
// One lives on the scene's "Audio" object (Tools > Audio > Wire Scene); one is created on demand if not.
public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance
    {
        get
        {
            if (instance == null && Application.isPlaying)
            {
                instance = FindAnyObjectByType<SoundManager>();
                if (instance == null) instance = new GameObject("Audio").AddComponent<SoundManager>();
            }
            return instance;
        }
    }
    static SoundManager instance;

    public AudioMixer mixer;
    [Tooltip("Pooled voices. When all are busy the oldest, least important one is stolen.")]
    public int voices = 32;
    public const float SpeedOfSound = 343f;

    [Header("Air Absorption")]
    [Tooltip("No filtering closer than this (m).")]
    public float lowPassStart = 40f;
    [Tooltip("Distance over which the cutoff falls by a factor of e (m).")]
    public float lowPassFalloff = 350f;
    public float minCutoff = 1200f;

    [Header("Snapshots")]
    [Tooltip("The player's vehicle entry; InVehicle plays while it has a car. Found on the listener's player if empty.")]
    public VehicleEntry vehicleEntry;
    public float snapshotBlend = 0.4f;

    [Header("Debug")]
    [Tooltip("Log every sound started (clip, layer, distance, delay, low-pass) to the console.")]
    public bool logPlays;

    class Voice
    {
        public AudioSource source;
        public AudioLowPassFilter lowPass;
        public Transform follow;
        public float busyUntil;
        public float startTime;
        public int priority;
        public SoundEvent ev;
    }

    Voice[] pool;
    AudioListener listener;
    bool indoor;
    string snapshot;

    void Awake()
    {
        if (instance != null && instance != this) { Destroy(this); return; }
        instance = this;
        pool = new Voice[voices];
        for (int i = 0; i < voices; i++)
        {
            var go = new GameObject("Voice " + i);
            go.transform.SetParent(transform, false);
            var v = new Voice { source = go.AddComponent<AudioSource>(), lowPass = go.AddComponent<AudioLowPassFilter>() };
            v.source.playOnAwake = false;
            v.source.dopplerLevel = 0f;
            v.source.rolloffMode = AudioRolloffMode.Logarithmic;
            v.lowPass.enabled = false;
            pool[i] = v;
        }
    }

    void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    public AudioListener Listener
    {
        get
        {
            if (listener == null || !listener.isActiveAndEnabled) listener = FindAnyObjectByType<AudioListener>();
            return listener;
        }
    }

    // Distance from the listener (infinite if there's none).
    public float ListenerDistance(Vector3 position)
    {
        var l = Listener;
        return l != null ? Vector3.Distance(l.transform.position, position) : float.PositiveInfinity;
    }

    public static void Play(SoundEvent ev, Vector3 position, float volumeScale = 1f, float pitchScale = 1f) =>
        Instance?.PlayInternal(ev, position, null, volumeScale, pitchScale);

    // Follows `target` while it plays (for sounds made by something moving).
    public static void PlayAttached(SoundEvent ev, Transform target, float volumeScale = 1f, float pitchScale = 1f)
    {
        if (target != null) Instance?.PlayInternal(ev, target.position, target, volumeScale, pitchScale);
    }

    void PlayInternal(SoundEvent ev, Vector3 position, Transform follow, float volumeScale, float pitchScale)
    {
        if (ev == null || pool == null) return;
        bool spatial = ev.spatialBlend > 0.5f;
        float distance = spatial ? ListenerDistance(position) : 0f;
        if (spatial && distance > ev.maxDistance) return;

        float vol = Random.Range(ev.volume.x, ev.volume.y) * volumeScale;
        float pitch = Random.Range(ev.pitch.x, ev.pitch.y) * pitchScale;
        float delay = spatial && distance < float.PositiveInfinity ? distance / SpeedOfSound : 0f;
        float cutoff = spatial ? Cutoff(distance) : 22000f;

        if (!ev.HasLayers || !spatial)
        {
            StartVoice(ev, 0, ev.Pick(0), position, follow, vol, pitch, delay, cutoff);
            return;
        }
        ev.LayerWeights(distance, out float close, out float mid, out float far);
        if (close > 0.01f) StartVoice(ev, 0, ev.Pick(0), position, follow, vol * close, pitch, delay, cutoff);
        if (mid > 0.01f) StartVoice(ev, 1, ev.Pick(1), position, follow, vol * mid, pitch, delay, cutoff);
        if (far > 0.01f) StartVoice(ev, 2, ev.Pick(2), position, follow, vol * far, pitch, delay, cutoff);
    }

    float Cutoff(float distance) =>
        distance <= lowPassStart ? 22000f : Mathf.Max(minCutoff, 22000f * Mathf.Exp(-(distance - lowPassStart) / lowPassFalloff));

    void StartVoice(SoundEvent ev, int layer, AudioClip clip, Vector3 position, Transform follow, float volume, float pitch, float delay, float cutoff)
    {
        if (clip == null) return;
        var v = TakeVoice(ev);
        if (v == null) return;
        var s = v.source;
        s.Stop();
        s.clip = clip;
        s.outputAudioMixerGroup = ev.output;
        s.volume = volume;
        s.pitch = pitch;
        s.spatialBlend = ev.spatialBlend;
        s.minDistance = ev.LayerMinDistance(layer);
        s.maxDistance = Mathf.Max(ev.maxDistance, s.minDistance + 0.01f);
        s.priority = ev.priority;
        s.transform.position = position;
        v.lowPass.enabled = cutoff < 21000f;
        v.lowPass.cutoffFrequency = cutoff;
        v.follow = follow;
        v.priority = ev.priority;
        v.ev = ev;
        v.startTime = Time.unscaledTime;
        v.busyUntil = Time.unscaledTime + delay + clip.length / Mathf.Max(0.1f, pitch) + 0.05f;
        if (delay > 0.002f) s.PlayDelayed(delay);
        else s.Play();
        if (logPlays)
            Debug.Log($"[Sound] {ev.name} layer {layer} {clip.name} vol {volume:0.00} pitch {pitch:0.00} " +
                      $"dist {ListenerDistance(position):0.0} m delay {delay * 1000f:0} ms cutoff {cutoff:0} Hz");
    }

    Voice TakeVoice(SoundEvent ev)
    {
        int priority = ev.priority;
        float now = Time.unscaledTime;
        // At its own limit, an event replaces its oldest voice (e.g. full auto never floods the pool).
        if (ev.maxVoices > 0)
        {
            Voice oldest = null;
            int count = 0;
            foreach (var v in pool)
            {
                if (v.ev != ev || now >= v.busyUntil) continue;
                count++;
                if (oldest == null || v.startTime < oldest.startTime) oldest = v;
            }
            if (count >= ev.maxVoices) return oldest;
        }
        Voice steal = null;
        foreach (var v in pool)
        {
            if (now >= v.busyUntil) return v;
            // Steal the least important (highest number), then the oldest.
            if (v.priority < priority) continue;
            if (steal == null || v.priority > steal.priority || (v.priority == steal.priority && v.startTime < steal.startTime)) steal = v;
        }
        return steal;
    }

    void LateUpdate()
    {
        if (pool == null) return;
        float now = Time.unscaledTime;
        foreach (var v in pool)
        {
            if (v.follow == null) continue;
            if (now >= v.busyUntil) { v.follow = null; continue; }
            v.source.transform.position = v.follow.position;
        }
        UpdateSnapshot();
    }

    // ---------- Snapshots ----------
    // Indoor while something says so (SetIndoor) or the listener stands in a House's rooms.
    public void SetIndoor(bool on) => indoor = on;
    bool inHouse;
    float nextHouseCheck;

    void UpdateSnapshot()
    {
        if (mixer == null) return;
        if (vehicleEntry == null && Listener != null) vehicleEntry = Listener.GetComponentInParent<VehicleEntry>();
        if (Time.unscaledTime >= nextHouseCheck)
        {
            nextHouseCheck = Time.unscaledTime + 0.2f;
            inHouse = Listener != null && House.At(Listener.transform.position) != null;
        }
        string want = Time.timeScale == 0f ? "Paused"
            : vehicleEntry != null && vehicleEntry.Car != null ? "InVehicle"
            : indoor || inHouse ? "Indoor" : "Outdoor";
        if (want == snapshot) return;
        var s = mixer.FindSnapshot(want);
        if (s == null) return;
        snapshot = want;
        s.TransitionTo(snapshotBlend);
    }
}
