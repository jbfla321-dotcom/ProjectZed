using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

// Review screenshots of the weather and time of day (Docs/Weather/*.png): the same few views (a village street,
// looking up at the sky, and inside a house toward a sunlit window) in each preset and at a few times.
// Tools > Environment > Capture Weather, or -executeMethod WeatherCapture.CaptureFromCommandLine. Not saved.
public static class WeatherCapture
{
    const string Out = "Docs/Weather";

    static readonly (string name, string preset, float hour)[] Shots =
    {
        ("sunny_noon", "Sunny", 13f), ("sunny_evening", "Sunny", 18.8f), ("cloudy_morning", "Cloudy", 9.5f),
        ("overcast_noon", "Overcast", 13f), ("foggy_morning", "Foggy", 8f), ("rain_noon", "Rain", 13f),
        ("storm_evening", "Storm", 18f), ("clear_night", "Sunny", 23.5f),
    };

    [MenuItem("Tools/Environment/Capture Weather")]
    public static void Capture()
    {
        Directory.CreateDirectory(Out);
        var wm = Object.FindAnyObjectByType<WeatherManager>();
        var tod = Object.FindAnyObjectByType<TimeOfDay>();
        if (wm == null || tod == null) { Debug.LogError("[WeatherCapture] Run Tools > Environment > Setup Weather first."); return; }
        var house = Object.FindObjectsByType<House>(FindObjectsInactive.Exclude).Where(h => h.type == "Rambler").OrderBy(h => h.transform.position.x).FirstOrDefault()
                    ?? Object.FindAnyObjectByType<House>();
        foreach (var (name, preset, hour) in Shots)
        {
            wm.SetPreset(preset, 0f);
            tod.SetTime(hour);
            wm.ApplyNow();
            if (house != null)
            {
                var t = house.transform;
                Vector3 eye = t.TransformPoint(new Vector3(-14f, 1.7f, 16f));
                eye.y = Ground(eye) + 1.7f;
                Debug.Log($"[WeatherCapture] {name}: house {house.transform.parent.parent.name}/{house.name} eye {eye} fog {RenderSettings.fog} {RenderSettings.fogDensity:F4} {RenderSettings.fogColor} ambient {RenderSettings.ambientProbe[0, 0]:F3}");
                Shoot(eye, t.TransformPoint(new Vector3(0f, 3f, 0f)), $"{Out}/{name}_street.png", 65f);
                Shoot(eye, eye + t.TransformDirection(new Vector3(0.3f, 0.6f, -1f)) * 100f, $"{Out}/{name}_sky.png", 75f);
                var room = house.rooms.OrderByDescending(b => b.size.x * b.size.z).First();
                Vector3 inside = t.TransformPoint(new Vector3(room.center.x, room.min.y + 1.6f, room.min.z + 0.5f));
                Shoot(inside, t.TransformPoint(new Vector3(room.center.x, room.min.y + 1f, room.max.z)), $"{Out}/{name}_inside.png", 75f);
            }
        }
        wm.SetPreset("Overcast", 0f);
        tod.SetTime(10.5f);
        Debug.Log("[WeatherCapture] Done");
    }

    // Which fog layers work: sky haze and volumetric fog each on/off in the Foggy preset.
    public static void FogTestFromCommandLine()
    {
        EditorSceneManager.OpenScene("Assets/ProjectZed.unity");
        Directory.CreateDirectory(Out + "/test");
        var wm = Object.FindAnyObjectByType<WeatherManager>();
        var tod = Object.FindAnyObjectByType<TimeOfDay>();
        wm.SetPreset("Foggy", 0f);
        tod.SetTime(9f);
        wm.ApplyNow();
        var profile = wm.volume.sharedProfile;
        profile.TryGet(out Fog fog);
        profile.TryGet(out VolumetricFogVolumeComponent vfog);
        Debug.Log($"[FogTest] fog enabled {fog.enabled.value} mfp {fog.meanFreePath.value} base {fog.baseHeight.value} max {fog.maximumHeight.value}; vfog enabled {vfog.enabled.value} density {vfog.density.value} dist {vfog.distance.value} h {vfog.baseHeight.value}..{vfog.maximumHeight.value}");
        var house = Object.FindAnyObjectByType<House>();
        Vector3 eye = house.transform.TransformPoint(new Vector3(-14f, 0f, 16f));
        eye.y = Ground(eye) + 1.7f;
        Vector3 at = house.transform.position + Vector3.up * 3f;
        int n = 0;
        foreach (var skyOn in new[] { false, true })
            foreach (var volOn in new[] { false, true })
            {
                fog.enabled.Override(skyOn);
                vfog.enabled.Override(volOn);
                Shoot(eye, at, $"{Out}/test/fog{n++}_sky{skyOn}_vol{volOn}.png", 65f);
            }
    }

    // Cloud quality A/B: the same skies at three settings, with a rough frame time per setting (Docs/Weather/clouds).
    public static void CloudABFromCommandLine()
    {
        EditorSceneManager.OpenScene("Assets/ProjectZed.unity");
        string dir = Out + "/clouds";
        Directory.CreateDirectory(dir);
        var wm = Object.FindAnyObjectByType<WeatherManager>();
        var tod = Object.FindAnyObjectByType<TimeOfDay>();
        var rd = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/PC_Renderer.asset");
        var feature = rd.rendererFeatures.First(f => f != null && f.GetType().Name == "VolumetricCloudsURP");
        var house = Object.FindObjectsByType<House>(FindObjectsInactive.Exclude).Where(h => h.type == "Rambler").OrderBy(h => h.transform.position.x).First();
        Vector3 eye = house.transform.TransformPoint(new Vector3(-14f, 0f, 16f));
        eye.y = Ground(eye) + 1.7f;
        var variants = new (string name, float res, int upscale, bool micro, int steps, int light, float density)[]
        {
            ("A_before", 0.5f, 0, false, 48, 4, 0.75f),
            ("B_sharp75", 0.75f, 1, true, 64, 6, 1f),
            ("C_sharp100", 1f, 1, true, 96, 8, 1f),
        };
        var skies = new (string name, string preset, float hour, Vector3 look)[]
        {
            ("cloudy", "Cloudy", 11f, new Vector3(0.4f, 0.45f, 1f)),
            ("overcast", "Overcast", 13f, new Vector3(-0.3f, 0.35f, 1f)),
            ("evening", "Cloudy", 18.3f, new Vector3(-1f, 0.25f, 0.2f)),
        };
        foreach (var v in variants)
        {
            var so = new SerializedObject(feature);
            so.FindProperty("resolutionScale").floatValue = v.res;
            so.FindProperty("upscaleMode").enumValueIndex = v.upscale;
            so.ApplyModifiedPropertiesWithoutUndo();
            feature.Create();
            wm.microErosion = v.micro; wm.cloudSteps = v.steps; wm.cloudLightSteps = v.light; wm.cloudDensityScale = v.density;
            foreach (var sky in skies)
            {
                wm.SetPreset(sky.preset, 0f);
                tod.SetTime(sky.hour);
                wm.ApplyNow();
                float ms = Shoot(eye, eye + sky.look * 100f, $"{dir}/{sky.name}_{v.name}.png", 70f);
                Debug.Log($"[CloudAB] {v.name} {sky.name}: {ms:F1} ms per frame");
            }
        }
    }

    public static void CaptureFromCommandLine()
    {
        EditorSceneManager.OpenScene("Assets/ProjectZed.unity");
        Capture();
    }

    static float Ground(Vector3 p)
    {
        if (Physics.Raycast(p + Vector3.up * 100f, Vector3.down, out var hit, 300f)) return hit.point.y;
        return p.y;
    }

    // Renders several frames so the clouds' temporal accumulation settles.
    static float Shoot(Vector3 from, Vector3 at, string path, float fov)
    {
        var go = new GameObject("WeatherCaptureCam");
        var cam = go.AddComponent<Camera>();
        cam.fieldOfView = fov;
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 20000f;
        cam.allowHDR = true;
        var data = cam.GetUniversalAdditionalCameraData();
        data.renderPostProcessing = true;
        data.requiresDepthTexture = true;
        go.transform.position = from;
        go.transform.LookAt(at);
        var rt = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        var rain = Object.FindAnyObjectByType<RainSystem>();
        var eyeAdapt = Object.FindAnyObjectByType<EyeAdaptation>();
        if (eyeAdapt != null) eyeAdapt.Evaluate(from, true);
        for (int i = 0; i < 12; i++)
        {
            if (rain != null) rain.Render(cam);   // queued for this camera only; each render uses up its queue
            cam.Render();
        }
        // Rough frame time: a few more frames, each waited on by reading a pixel back
        var probe = new Texture2D(1, 1, TextureFormat.RGB24, false);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 8; i++)
        {
            cam.Render();
            RenderTexture.active = rt;
            probe.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
            probe.Apply();
        }
        float ms = (float)sw.Elapsed.TotalMilliseconds / 8f;
        Object.DestroyImmediate(probe);
        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        RenderTexture.active = null;
        cam.targetTexture = null;
        Object.DestroyImmediate(rt);
        Object.DestroyImmediate(tex);
        Object.DestroyImmediate(go);
        Debug.Log("[WeatherCapture] " + path);
        return ms;
    }
}
