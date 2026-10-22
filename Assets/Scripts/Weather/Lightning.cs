using System.Collections;
using UnityEngine;

// Storm lightning: now and then the sky flashes (a second, shadowless directional light flickering from somewhere
// overhead) and thunder rolls in from the strike's distance. SoundManager delays sounds by distance / 343 m/s, so a
// strike 1 km away is heard about 3 s after the flash. WeatherManager sets storm (0-1).
public class Lightning : MonoBehaviour
{
    [Range(0f, 1f)] public float storm;
    [Tooltip("Seconds between strikes at storm 0.1 and 1.")]
    public Vector2 interval = new Vector2(45f, 6f);
    [Tooltip("Strike distance from the listener (m).")]
    public Vector2 distance = new Vector2(250f, 3500f);
    public float flashIntensity = 6f;
    public Color flashColor = new Color(0.82f, 0.86f, 1f);
    public SoundEvent thunder;

    Light flash;
    float next;

    void OnEnable()
    {
        if (flash == null)
        {
            var go = new GameObject("LightningFlash") { hideFlags = HideFlags.DontSave };
            go.transform.SetParent(transform, false);
            flash = go.AddComponent<Light>();
            flash.type = LightType.Directional;
            flash.shadows = LightShadows.None;
            flash.color = flashColor;
            flash.intensity = 0f;
            flash.enabled = false;
        }
        next = Time.time + 5f;
    }

    void OnDisable()
    {
        if (flash != null) Destroy(flash.gameObject);
        flash = null;
    }

    void Update()
    {
        if (storm < 0.05f || Time.time < next) return;
        next = Time.time + Mathf.Lerp(interval.x, interval.y, storm) * Random.Range(0.5f, 1.5f);
        StartCoroutine(Strike());
    }

    public void StrikeNow() => StartCoroutine(Strike());

    IEnumerator Strike()
    {
        var listener = SoundManager.Instance != null ? SoundManager.Instance.Listener : null;
        Vector3 centre = listener != null ? listener.transform.position : transform.position;
        float d = Mathf.Lerp(distance.x, distance.y, Mathf.Pow(Random.value, 0.7f));
        float ang = Random.value * Mathf.PI * 2f;
        Vector3 at = centre + new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang)) * d;
        if (thunder != null) SoundManager.Play(thunder, at + Vector3.up * 400f, Mathf.Lerp(1f, 0.5f, d / distance.y));

        // Light comes from up in the clouds above the strike
        flash.transform.rotation = Quaternion.LookRotation(centre - (at + Vector3.up * 1500f));
        float near = Mathf.Lerp(1f, 0.25f, Mathf.InverseLerp(distance.x, distance.y, d));
        flash.enabled = true;
        int flickers = Random.Range(2, 5);
        for (int i = 0; i < flickers; i++)
        {
            flash.intensity = flashIntensity * near * Random.Range(0.5f, 1f);
            yield return new WaitForSeconds(Random.Range(0.03f, 0.08f));
            flash.intensity = flashIntensity * near * 0.1f;
            yield return new WaitForSeconds(Random.Range(0.04f, 0.15f));
        }
        for (float t = 0f; t < 0.25f; t += Time.deltaTime)
        {
            flash.intensity = Mathf.Lerp(flashIntensity * near * 0.3f, 0f, t / 0.25f);
            yield return null;
        }
        flash.intensity = 0f;
        flash.enabled = false;
    }
}
