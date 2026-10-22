using UnityEngine;
using UnityEngine.Audio;

// The sound of rain: looping beds that follow how hard it's raining (a light shower bed fading into steady rain on
// the forest) and, inside a house, rain drumming on the roof and windows instead. Lives on the "Weather" object;
// reads the rain from WeatherManager and indoors from House.At (the listener's position).
public class RainAudio : MonoBehaviour
{
    public AudioClip lightLoop, heavyLoop, indoorLoop;
    [Range(0f, 1f)] public float volume = 0.8f;
    [Tooltip("Seconds to cross between the outdoor and indoor sound when you go through a door.")]
    public float indoorFade = 0.6f;

    AudioSource light, heavy, indoor;
    float indoorBlend;

    void OnEnable()
    {
        light = Source("RainLight", lightLoop);
        heavy = Source("RainHeavy", heavyLoop);
        indoor = Source("RainIndoor", indoorLoop);
    }

    AudioSource Source(string name, AudioClip clip)
    {
        var go = new GameObject(name) { hideFlags = HideFlags.DontSave };
        go.transform.SetParent(transform, false);
        var s = go.AddComponent<AudioSource>();
        s.clip = clip;
        s.loop = true;
        s.playOnAwake = false;
        s.spatialBlend = 0f;
        s.volume = 0f;
        var sm = SoundManager.Instance;
        if (sm != null && sm.mixer != null)
        {
            var groups = sm.mixer.FindMatchingGroups("Ambience");
            if (groups.Length > 0) s.outputAudioMixerGroup = groups[0];
        }
        return s;
    }

    void OnDisable()
    {
        foreach (var s in new[] { light, heavy, indoor }) if (s != null) Destroy(s.gameObject);
    }

    void Update()
    {
        var wm = WeatherManager.Instance;
        float rain = wm != null ? wm.current.rain : 0f;
        var listener = SoundManager.Instance != null ? SoundManager.Instance.Listener : null;
        bool inside = listener != null && House.At(listener.transform.position) != null;
        indoorBlend = Mathf.MoveTowards(indoorBlend, inside ? 1f : 0f, Time.deltaTime / Mathf.Max(0.05f, indoorFade));

        float outdoor = 1f - indoorBlend;
        Set(light, volume * Mathf.Clamp01(rain * 3f) * Mathf.Clamp01(1.4f - rain * 1.6f) * outdoor);
        Set(heavy, volume * Mathf.Clamp01((rain - 0.2f) * 1.6f) * outdoor);
        Set(indoor, volume * 0.85f * Mathf.Clamp01(rain * 1.6f) * indoorBlend);
    }

    static void Set(AudioSource s, float v)
    {
        if (s == null || s.clip == null) return;
        s.volume = v;
        if (v > 0.001f && !s.isPlaying) s.Play();
        else if (v <= 0.001f && s.isPlaying) s.Pause();
    }
}
