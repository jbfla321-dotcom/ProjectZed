using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

// Tools > Environment > Bake House Lighting: Adaptive Probe Volumes with sky occlusion over every site, so indoors
// is darker than outdoors in the right places (corners, rooms away from windows) while the light still follows the
// time of day and weather without rebaking (sky occlusion is multiplied by the live sky's ambient at runtime).
// The sun stays a realtime light, so its direct light and shadows through windows are live too.
//
// Sets the URP light probe system to probe volumes, marks houses / furniture / terrain as contributing to GI (lit
// by probes, no lightmaps), puts a local Probe Volume around each site and bakes probes only (no lightmaps).
// Re-run after Build Houses. Headless: -executeMethod HouseLightingBake.BakeFromCommandLine (without -quit; the
// editor exits when the bake finishes).
public static class HouseLightingBake
{
    const string Dir = "Assets/Environment/Weather";

    [MenuItem("Tools/Environment/Bake House Lighting")]
    public static void Bake() => Bake(false);

    public static void BakeFromCommandLine()
    {
        EditorSceneManager.OpenScene("Assets/ProjectZed.unity");
        Bake(true);
    }

    static void Bake(bool exitWhenDone)
    {
        if (EditorApplication.isPlaying) { Debug.LogError("[HouseLightingBake] Stop Play mode first."); return; }
        var scene = EditorSceneManager.GetActiveScene();

        // URP: probe volumes
        foreach (var path in new[] { "Assets/Settings/PC_RPAsset.asset" })
        {
            var asset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(path);
            if (asset == null) continue;
            var so = new SerializedObject(asset);
            so.FindProperty("m_LightProbeSystem").intValue = 1;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        // What occludes the sky: houses, furniture, terrain
        int marked = 0;
        foreach (var house in Object.FindObjectsByType<House>(FindObjectsInactive.Exclude))
            foreach (var r in house.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (r.GetComponent<BreakableGlass>() != null || r.GetComponentInParent<Door>() != null) continue;
                var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject) | StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic;
                GameObjectUtility.SetStaticEditorFlags(r.gameObject, flags);
                r.receiveGI = ReceiveGI.LightProbes;
                marked++;
            }
        foreach (var t in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude))
            GameObjectUtility.SetStaticEditorFlags(t.gameObject, GameObjectUtility.GetStaticEditorFlags(t.gameObject) | StaticEditorFlags.ContributeGI);

        // A probe volume around each site's houses
        var root = GameObject.Find("Weather/ProbeVolumes") ?? new GameObject("ProbeVolumes");
        var weather = GameObject.Find("Weather");
        if (weather != null) root.transform.SetParent(weather.transform, false);
        for (int i = root.transform.childCount - 1; i >= 0; i--) Object.DestroyImmediate(root.transform.GetChild(i).gameObject);
        var locations = GameObject.Find("World/Locations");
        int volumes = 0;
        if (locations != null)
            foreach (Transform site in locations.transform)
            {
                var houses = site.GetComponentsInChildren<House>();
                if (houses.Length == 0) continue;
                var b = new Bounds(houses[0].transform.position, Vector3.zero);
                foreach (var h in houses)
                    foreach (var r in h.GetComponentsInChildren<MeshRenderer>()) b.Encapsulate(r.bounds);
                b.Expand(new Vector3(8f, 4f, 8f));
                var go = new GameObject("ProbeVolume_" + site.name);
                go.transform.SetParent(root.transform, false);
                go.transform.position = b.center;
                var pv = go.AddComponent<ProbeVolume>();
                pv.mode = ProbeVolume.Mode.Local;
                pv.size = b.size;
                volumes++;
            }

        // Baking set: sky occlusion on, fine probes near geometry
        string setPath = $"{Dir}/APV_BakingSet.asset";
        var set = AssetDatabase.LoadAssetAtPath<ProbeVolumeBakingSet>(setPath);
        if (set == null)
        {
            set = ScriptableObject.CreateInstance<ProbeVolumeBakingSet>();
            set.name = "APV_BakingSet";
            AssetDatabase.CreateAsset(set, setPath);
        }
        set.skyOcclusion = true;
        set.skyOcclusionBakingSamples = 1024;
        set.skyOcclusionBakingBounces = 2;
        set.minDistanceBetweenProbes = 1f;
        set.simplificationLevels = 3;
        set.TryAddScene(AssetDatabase.AssetPathToGUID(scene.path));
        EditorUtility.SetDirty(set);

        // Lighting settings: GPU lightmapper (sky occlusion needs it), baked GI for probes, no realtime GI
        string lsPath = $"{Dir}/WorldLighting.lighting";
        var ls = AssetDatabase.LoadAssetAtPath<LightingSettings>(lsPath);
        if (ls == null)
        {
            ls = new LightingSettings { name = "WorldLighting" };
            AssetDatabase.CreateAsset(ls, lsPath);
        }
        ls.bakedGI = true;
        ls.realtimeGI = false;
        ls.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU;
        ls.mixedBakeMode = MixedLightingMode.IndirectOnly;
        Lightmapping.lightingSettings = ls;
        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[HouseLightingBake] {marked} renderers contribute to GI, {volumes} probe volumes. Baking...");

        // The probes keep the baked daytime sky bounce as fill light; WeatherManager scales it down at night and
        // under cloud (Probe Volumes Options intensity), on top of the live sky occlusion.
        float ambientBefore = RenderSettings.ambientIntensity;

        // The probe system only initialises once the pipeline has rendered with probe volumes on, so render a frame
        // and retry for a while before giving up.
        int tries = 0;
        bool started = false;
        double start = EditorApplication.timeSinceStartup;
        void Poll()
        {
            if (!started)
            {
                var go = new GameObject("BakeWarmupCam");
                var cam = go.AddComponent<Camera>();
                var rt = new RenderTexture(64, 64, 24);
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Object.DestroyImmediate(go);
                started = AdaptiveProbeVolumes.BakeAsync();
                if (started) { start = EditorApplication.timeSinceStartup; return; }
                if (++tries < 120) return;
                EditorApplication.update -= Poll;
                Debug.LogError("[HouseLightingBake] Couldn't start the probe bake.");
                if (exitWhenDone) EditorApplication.Exit(1);
                return;
            }
            if (AdaptiveProbeVolumes.isRunning && EditorApplication.timeSinceStartup - start < 3600) return;
            EditorApplication.update -= Poll;
            bool ok = !AdaptiveProbeVolumes.isRunning;
            RenderSettings.ambientIntensity = ambientBefore;
            Debug.Log($"[HouseLightingBake] Bake {(ok ? "finished" : "timed out")} in {EditorApplication.timeSinceStartup - start:F0} s.");
            AssetDatabase.SaveAssets();
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            if (exitWhenDone) EditorApplication.Exit(ok ? 0 : 2);
        }
        EditorApplication.update += Poll;
    }
}
