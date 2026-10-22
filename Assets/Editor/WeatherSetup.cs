using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Tools > Environment > Setup Weather: wires the weather, day/night and sky into the project and scene. Idempotent.
//
// Project: HDR on, longer/finer sun shadows (crisp window shadows indoors), light cookies (cloud shadows), and the
// renderer features from the three MIT packages: Physically Based Sky URP and Volumetric Clouds URP (jiaozi158 /
// stencg) and URP Volumetric Fog (CristianQiu). Scripting defines for the clouds' shadows / denoise / depth.
// Assets (Assets/Environment/Weather): WeatherProfile (sky, haze fog, clouds, volumetric fog, tonemapping),
// a starfield cubemap for the night sky, rain materials.
// Scene: on the "Weather" object, TimeOfDay, WeatherManager, RainSystem, Lightning, WetSurfaces and a global Volume;
// the directional light becomes RenderSettings.sun; the old exp² fog is switched off (the sky's haze replaces it);
// cameras render post-processing (the volumetric fog needs it).
public static class WeatherSetup
{
    const string Dir = "Assets/Environment/Weather";
    const string CloudsMaterial = "Packages/com.jiaozi158.unity-volumetric-clouds-urp/Runtime/VolumetricClouds.mat";
    static readonly string[] Defines = { "VC_SHADOWS", "VC_DENOISE", "VC_DEPTH", "OPTIMISATION" };

    [MenuItem("Tools/Environment/Setup Weather")]
    public static void Setup()
    {
        if (EditorApplication.isPlaying) { Debug.LogError("[WeatherSetup] Stop Play mode first."); return; }
        EnsureFolder(Dir);
        foreach (var asset in UrpAssets()) ConfigurePipeline(asset);
        AddDefines();
        var profile = BuildProfile();
        SetupScene(profile);
        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[WeatherSetup] Done. F7 in Play mode opens the weather menu; 'weather' / 'time' in the dev console.");
    }

    public static void SetupFromCommandLine()
    {
        EditorSceneManager.OpenScene("Assets/ProjectZed.unity");
        Setup();
    }

    // ---------- Pipeline ----------
    static UniversalRenderPipelineAsset[] UrpAssets()
    {
        var list = new System.Collections.Generic.List<UniversalRenderPipelineAsset>();
        var pc = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/PC_RPAsset.asset");
        if (pc != null) list.Add(pc);
        if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset cur && !list.Contains(cur)) list.Add(cur);
        return list.ToArray();
    }

    static void ConfigurePipeline(UniversalRenderPipelineAsset asset)
    {
        var so = new SerializedObject(asset);
        so.FindProperty("m_SupportsHDR").boolValue = true;
        so.FindProperty("m_RequireDepthTexture").boolValue = true;
        so.FindProperty("m_SupportsLightCookies").boolValue = true;
        so.FindProperty("m_ShadowDistance").floatValue = 150f;
        so.FindProperty("m_ShadowCascadeCount").intValue = 4;
        so.FindProperty("m_Cascade4Split").vector3Value = new Vector3(0.04f, 0.12f, 0.35f);
        so.FindProperty("m_MainLightShadowmapResolution").intValue = 4096;
        so.ApplyModifiedPropertiesWithoutUndo();

        var data = so.FindProperty("m_RendererDataList");
        for (int i = 0; i < data.arraySize; i++)
        {
            if (!(data.GetArrayElementAtIndex(i).objectReferenceValue is UniversalRendererData rd)) continue;
            var rso = new SerializedObject(rd);
            rso.FindProperty("m_ShadowTransparentReceive").boolValue = true;
            rso.ApplyModifiedPropertiesWithoutUndo();
            var sky = AddFeature(rd, typeof(PhysicallyBasedSkyURP), "PhysicallyBasedSkyURP");
            var clouds = AddFeature(rd, typeof(VolumetricCloudsURP), "VolumetricCloudsURP");
            var vfogFeature = AddFeature(rd, typeof(VolumetricFogRendererFeature), "VolumetricFog");
            // The fog plugin only finds its shaders in the Editor; builds need them serialized or the screen goes black
            SetFields(vfogFeature, ("downsampleDepthShader", Shader.Find("Hidden/DownsampleDepth")), ("volumetricFogShader", Shader.Find("Hidden/VolumetricFog")));
            var cloudMat = AssetDatabase.LoadAssetAtPath<Material>(CloudsMaterial);
            SetFields(sky, ("m_Shader", Shader.Find("Hidden/Skybox/PhysicallyBasedSky")), ("m_LutShader", Shader.Find("Hidden/Sky/PhysicallyBasedSkyPrecomputation")),
                      ("m_VolumetricCloudsMaterial", cloudMat), ("m_FallbackSkyMaterial", RenderSettings.skybox));
            SetFields(clouds, ("material", cloudMat));
            var cso = new SerializedObject(clouds);
            cso.FindProperty("resolutionScale").floatValue = 0.75f;
            cso.FindProperty("upscaleMode").enumValueIndex = 1;   // bilateral: sharper edges than bilinear
            cso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(rd);
        }
        EditorUtility.SetDirty(asset);
    }

    static ScriptableRendererFeature AddFeature(UniversalRendererData rd, Type type, string name)
    {
        var existing = rd.rendererFeatures.FirstOrDefault(f => f != null && f.GetType() == type);
        if (existing != null) return existing;
        var feature = (ScriptableRendererFeature)ScriptableObject.CreateInstance(type);
        feature.name = name;
        AssetDatabase.AddObjectToAsset(feature, rd);
        AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out long localId);
        var so = new SerializedObject(rd);
        var list = so.FindProperty("m_RendererFeatures");
        list.arraySize++;
        list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = feature;
        var map = so.FindProperty("m_RendererFeatureMap");
        map.arraySize++;
        map.GetArrayElementAtIndex(map.arraySize - 1).longValue = localId;
        so.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log($"[WeatherSetup] Added renderer feature {name} to {rd.name}");
        return feature;
    }

    static void SetFields(UnityEngine.Object target, params (string field, UnityEngine.Object value)[] values)
    {
        var so = new SerializedObject(target);
        foreach (var (field, value) in values)
        {
            var p = so.FindProperty(field);
            if (p == null) { Debug.LogWarning($"[WeatherSetup] {target.name}: no field {field}"); continue; }
            p.objectReferenceValue = value;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }

    static void AddDefines()
    {
        var target = NamedBuildTarget.Standalone;
        var current = PlayerSettings.GetScriptingDefineSymbols(target).Split(';').Where(s => s.Length > 0).ToList();
        bool changed = false;
        foreach (var d in Defines) if (!current.Contains(d)) { current.Add(d); changed = true; }
        if (changed) PlayerSettings.SetScriptingDefineSymbols(target, string.Join(";", current));
    }

    // ---------- Volume profile ----------
    static VolumeProfile BuildProfile()
    {
        string path = $"{Dir}/WeatherProfile.asset";
        var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
        if (profile == null)
        {
            profile = ScriptableObject.CreateInstance<VolumeProfile>();
            AssetDatabase.CreateAsset(profile, path);
        }
        var env = Get<VisualEnvironment>(profile);
        env.skyType.Override((int)VisualEnvironment.SkyType.PhysicallyBased);
        env.skyAmbientMode.Override(VisualEnvironment.SkyAmbientMode.Dynamic);

        var sky = Get<PhysicallyBasedSky>(profile);
        sky.exposure.Override(0f);
        sky.spaceEmissionTexture.Override(Stars());
        sky.spaceEmissionMultiplier.Override(0.4f);
        sky.updateMode.Override(PhysicallyBasedSky.EnvironmentUpdateMode.OnChanged);

        var fog = Get<Fog>(profile);
        fog.enabled.Override(false);
        fog.baseHeight.Override(0f);
        fog.maximumHeight.Override(200f);
        fog.meanFreePath.Override(400f);

        var clouds = Get<VolumetricClouds>(profile);
        clouds.state.Override(true);
        // Custom preset so WeatherManager's shape values are used as they are
        var preset = typeof(VolumetricClouds).GetField("m_CloudPreset", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(clouds) as VolumeParameter<VolumetricClouds.CloudPresets>;
        preset?.Override(VolumetricClouds.CloudPresets.Custom);
        clouds.shadows.Override(true);
        clouds.numPrimarySteps.Override(48);
        clouds.numLightSteps.Override(4);
        clouds.temporalAccumulationFactor.Override(0.9f);

        var vfog = Get<VolumetricFogVolumeComponent>(profile);
        vfog.enabled.Override(true);
        vfog.enableMainLightContribution.Override(true);
        vfog.enableAPVContribution.Override(false);
        vfog.maxSteps.Override(64);
        vfog.baseHeight.Override(-50f);

        var apv = Get<ProbeVolumesOptions>(profile);
        apv.skyOcclusionIntensityMultiplier.Override(5f);   // rooms lit through windows read better
        apv.leakReductionMode.Override(APVLeakReductionMode.Quality);
        Get<ColorAdjustments>(profile).postExposure.Override(0f);   // EyeAdaptation drives it

        var tone = Get<Tonemapping>(profile);
        tone.mode.Override(TonemappingMode.ACES);
        var bloom = Get<Bloom>(profile);
        bloom.intensity.Override(0.25f);
        bloom.threshold.Override(1.2f);
        EditorUtility.SetDirty(profile);
        foreach (var c in profile.components) EditorUtility.SetDirty(c);
        return profile;
    }

    static T Get<T>(VolumeProfile profile) where T : VolumeComponent
    {
        if (!profile.TryGet(out T c))
        {
            c = profile.Add<T>(false);
            c.name = typeof(T).Name;
            AssetDatabase.AddObjectToAsset(c, profile);
        }
        c.active = true;
        return c;
    }

    // A starfield for the night sky: a few thousand points of varied brightness and a faint warm/cool tint.
    static Cubemap Stars()
    {
        string path = $"{Dir}/Stars.asset";
        var cube = AssetDatabase.LoadAssetAtPath<Cubemap>(path);
        if (cube != null) return cube;
        const int size = 512;
        cube = new Cubemap(size, TextureFormat.RGBAHalf, false);
        var black = new Color[size * size];
        var faces = new Color[6][];
        for (int f = 0; f < 6; f++) faces[f] = (Color[])black.Clone();
        var rng = new System.Random(1985);
        for (int i = 0; i < 9000; i++)
        {
            var d = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
            if (d.sqrMagnitude > 1f || d.sqrMagnitude < 1e-4f) continue;
            d.Normalize();
            float mag = Mathf.Pow((float)rng.NextDouble(), 6f) * 6f + 0.08f;
            float tint = (float)rng.NextDouble();
            var c = new Color(Mathf.Lerp(0.8f, 1f, tint), 0.9f, Mathf.Lerp(1f, 0.75f, tint)) * mag;
            Face(d, size, out int face, out int x, out int y);
            faces[face][y * size + x] += c;
        }
        for (int f = 0; f < 6; f++) cube.SetPixels(faces[f], (CubemapFace)f);
        cube.Apply(false);
        AssetDatabase.CreateAsset(cube, path);
        return cube;
    }

    static void Face(Vector3 d, int size, out int face, out int x, out int y)
    {
        float ax = Mathf.Abs(d.x), ay = Mathf.Abs(d.y), az = Mathf.Abs(d.z);
        float u, v;
        if (ax >= ay && ax >= az) { face = d.x > 0 ? 0 : 1; u = d.x > 0 ? -d.z / ax : d.z / ax; v = -d.y / ax; }
        else if (ay >= az) { face = d.y > 0 ? 2 : 3; u = d.x / ay; v = d.y > 0 ? d.z / ay : -d.z / ay; }
        else { face = d.z > 0 ? 4 : 5; u = d.z > 0 ? d.x / az : -d.x / az; v = -d.y / az; }
        x = Mathf.Clamp((int)((u * 0.5f + 0.5f) * size), 0, size - 1);
        y = Mathf.Clamp((int)((v * 0.5f + 0.5f) * size), 0, size - 1);
    }

    // ---------- Scene ----------
    static void SetupScene(VolumeProfile profile)
    {
        var weather = GameObject.Find("Weather") ?? new GameObject("Weather");
        Light sun = RenderSettings.sun;
        if (sun == null) sun = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude).FirstOrDefault(l => l.type == LightType.Directional);
        if (sun != null)
        {
            RenderSettings.sun = sun;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 1f;
            sun.shadowBias = 0.05f;
            sun.shadowNormalBias = 0.3f;
        }
        RenderSettings.fog = true;    // distance haze; WeatherManager drives its density and colour
        RenderSettings.fogMode = FogMode.ExponentialSquared;

        var tod = Ensure<TimeOfDay>(weather);
        tod.sun = sun;
        var wm = Ensure<WeatherManager>(weather);
        var rain = Ensure<RainSystem>(weather);
        rain.rainMaterial = Mat("M_Rain", "ProjectZed/Rain");
        rain.splashMaterial = Mat("M_RainSplash", "ProjectZed/RainSplash");
        var lightning = Ensure<Lightning>(weather);
        lightning.thunder = AssetDatabase.LoadAssetAtPath<SoundEvent>("Assets/Audio/Events/Thunder.asset");
        Ensure<EyeAdaptation>(weather);
        var rainAudio = Ensure<RainAudio>(weather);
        rainAudio.lightLoop = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/Ambience/Rain_Light_Loop_01.wav");
        rainAudio.heavyLoop = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/Ambience/Rain_Outdoor_Loop_01.wav");
        rainAudio.indoorLoop = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/Audio/Ambience/Rain_Indoor_Loop_01.wav");
        var wet = Ensure<WetSurfaces>(weather);
        wet.materials.Clear();   // collected when Play starts (after houses / forest rebuilds)
        wet.terrainLayers.Clear();

        var volGo = weather.transform.Find("WeatherVolume")?.gameObject;
        if (volGo == null)
        {
            volGo = new GameObject("WeatherVolume");
            volGo.transform.SetParent(weather.transform, false);
        }
        var vol = Ensure<Volume>(volGo);
        vol.isGlobal = true;
        vol.priority = 10f;
        vol.sharedProfile = profile;
        wm.volume = vol;
        wm.rain = rain;
        wm.lightning = lightning;
        wm.wetSurfaces = wet;

        // Floating labels and signs: depth-writing text so clouds and fog don't cover them
        var textShader = Shader.Find("ProjectZed/WorldText");
        foreach (var tm in UnityEngine.Object.FindObjectsByType<TextMesh>(FindObjectsInactive.Include))
        {
            var wt = Ensure<WorldText>(tm.gameObject);
            wt.shader = textShader;
            EditorUtility.SetDirty(wt);
        }

        foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include))
        {
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            EditorUtility.SetDirty(data);
        }
    }

    static T Ensure<T>(GameObject go) where T : Component => go.GetComponent<T>() ?? go.AddComponent<T>();

    static Material Mat(string name, string shader)
    {
        string path = $"{Dir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find(shader));
            AssetDatabase.CreateAsset(m, path);
        }
        return m;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = System.IO.Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(path));
    }
}
