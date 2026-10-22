using UnityEngine;
using UnityEngine.Audio;

// One kind of sound (a gunshot, a footstep on gravel): its variations and how they're played.
// SoundManager.Play picks a clip, randomises volume and pitch, and for 3D sounds with distance layers
// crossfades from the close recording to the mid and far ones as the listener gets further away.
// Assets live in Assets/Audio/Events and are built by Tools > Audio (AudioTool).
[CreateAssetMenu(menuName = "ProjectZed/Audio/Sound Event")]
public class SoundEvent : ScriptableObject
{
    public AudioClip[] clips;
    public AudioMixerGroup output;

    [Header("Variation")]
    public Vector2 volume = new Vector2(0.9f, 1f);
    public Vector2 pitch = new Vector2(0.95f, 1.05f);
    [Tooltip("Never play the same clip twice in a row.")]
    public bool noImmediateRepeat = true;
    [Tooltip("0 = highest. Lower-priority voices are stolen first when the pool is full.")]
    [Range(0, 256)] public int priority = 128;
    [Tooltip("Most voices this event may use at once (0 = no limit); a new one replaces its oldest.")]
    public int maxVoices;

    [Header("3D")]
    [Range(0f, 1f)] public float spatialBlend = 1f;
    [Tooltip("Close layer: full volume within this distance, then logarithmic falloff.")]
    public float minDistance = 1f;
    [Tooltip("Not played at all beyond this distance.")]
    public float maxDistance = 50f;

    [Header("Distance Layers (optional)")]
    [Tooltip("Recordings made further away (e.g. a shot from 50 m). Takes over from the close layer around Mid Distance.")]
    public AudioClip[] midClips;
    public float midDistance = 60f;
    [Tooltip("Mid layer's full-volume distance (roughly how far away it was recorded).")]
    public float midMinDistance = 30f;
    [Tooltip("Distant recordings (e.g. 200 m). Take over around Far Distance.")]
    public AudioClip[] farClips;
    public float farDistance = 300f;
    public float farMinDistance = 150f;
    [Tooltip("Width (m) of each crossfade between layers, centred on the layer distance.")]
    public float crossfade = 60f;

    int lastClip = -1, lastMid = -1, lastFar = -1;

    public bool HasLayers => (midClips != null && midClips.Length > 0) || (farClips != null && farClips.Length > 0);

    // Layer 0 = close, 1 = mid, 2 = far.
    public AudioClip Pick(int layer)
    {
        switch (layer)
        {
            case 1: return Pick(midClips, ref lastMid);
            case 2: return Pick(farClips, ref lastFar);
            default: return Pick(clips, ref lastClip);
        }
    }

    AudioClip Pick(AudioClip[] set, ref int last)
    {
        if (set == null || set.Length == 0) return null;
        int i = Random.Range(0, set.Length);
        if (noImmediateRepeat && set.Length > 1 && i == last) i = (i + 1 + Random.Range(0, set.Length - 1)) % set.Length;
        last = i;
        return set[i];
    }

    public float LayerMinDistance(int layer) => layer == 1 ? midMinDistance : layer == 2 ? farMinDistance : minDistance;

    // How loud each layer is at this distance (they sum to 1). Missing layers hand over to the next one.
    public void LayerWeights(float distance, out float close, out float mid, out float far)
    {
        bool hasMid = midClips != null && midClips.Length > 0;
        bool hasFar = farClips != null && farClips.Length > 0;
        float toMid = hasMid ? Ramp(distance, midDistance) : 0f;
        float toFar = hasFar ? Ramp(distance, farDistance) : 0f;
        if (!hasMid) { close = 1f - toFar; mid = 0f; far = toFar; return; }
        close = 1f - toMid;
        mid = toMid * (1f - toFar);
        far = toMid * toFar;
    }

    float Ramp(float d, float centre)
    {
        float half = Mathf.Max(crossfade * 0.5f, 0.01f);
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(centre - half, centre + half, d));
    }
}
