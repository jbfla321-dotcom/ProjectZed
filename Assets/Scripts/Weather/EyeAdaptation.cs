using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Poor man's eye adaptation (URP has no auto exposure): when you step inside a house the exposure rises over a
// second or two, so rooms lit only through their windows are readable and the windows glow; at night it rises a
// little outdoors too. Drives Color Adjustments' post exposure on the weather Volume. Lives on the "Weather" object.
public class EyeAdaptation : MonoBehaviour
{
    [Tooltip("Extra exposure (stops) inside a house by day, and outdoors / indoors at night.")]
    public float indoorDay = 3f, outdoorNight = 0.6f, indoorNight = 1f;
    [Tooltip("Seconds to adapt going in and coming out.")]
    public float adaptIn = 1.5f, adaptOut = 0.8f;

    float exposure;
    ColorAdjustments color;
    Volume volume;

    void OnEnable() => Bind();

    void Bind()
    {
        var wm = GetComponent<WeatherManager>();
        volume = wm != null ? wm.volume : null;
        if (volume == null) return;
        var profile = Application.isPlaying ? volume.profile : volume.sharedProfile;
        if (!profile.TryGet(out color)) { color = profile.Add<ColorAdjustments>(false); }
        color.postExposure.overrideState = true;
    }

    void Update()
    {
        var cam = Camera.main;
        if (cam != null) Evaluate(cam.transform.position, false);
    }

    // Target exposure for an eye at this position; instant skips the adaptation (editor captures).
    public void Evaluate(Vector3 eye, bool instant)
    {
        if (color == null) Bind();
        if (color == null) return;
        var tod = TimeOfDay.Instance;
        float night = tod != null ? 1f - tod.Daylight : 0f;
        bool inside = House.At(eye) != null;
        float target = inside ? Mathf.Lerp(indoorDay, indoorNight, night) : outdoorNight * night;
        if (instant) exposure = target;
        else exposure = Mathf.MoveTowards(exposure, target, Time.deltaTime * 2f / (target > exposure ? adaptIn : adaptOut));
        color.postExposure.value = exposure;
    }
}
