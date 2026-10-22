using System.Collections.Generic;
using UnityEngine;

// Things outside get wet in the rain and dry slowly afterwards: darker and glossier (Lagarde's "water drop" recipe:
// porous surfaces darken, smooth ones gain gloss). Works on the outdoor materials (house exteriors, roads, rocks and
// logs) and the terrain layers by adjusting their colour and smoothness; the originals are put back when this is
// disabled (so Play mode never leaves assets changed). Also sets the global _PZ_Wetness for shaders that want it.
[DefaultExecutionOrder(-30)]
public class WetSurfaces : MonoBehaviour
{
    [Range(0f, 1f), Tooltip("How hard it's raining now (set by WeatherManager).")]
    public float raining;
    [Range(0f, 1f)] public float wetness;
    [Tooltip("Seconds to get soaked in heavy rain, and to dry out again.")]
    public float wetTime = 60f, dryTime = 600f;
    [Tooltip("Colour multiplier when soaked.")]
    public float darken = 0.62f;
    [Tooltip("Smoothness when soaked (materials without a smoothness map).")]
    public float wetSmoothness = 0.78f;
    [Tooltip("Materials whose names start with one of these are outdoor and get wet.")]
    public string[] materialPrefixes = { "H_Ext_", "M_Road_", "Bark_", "Prop_" };
    public List<Material> materials = new List<Material>();
    public List<TerrainLayer> terrainLayers = new List<TerrainLayer>();

    struct MatOriginal { public Color color; public float smoothness; }
    struct LayerOriginal { public Vector4 diffuseMax; public float smoothness; public Vector4 maskMax; }
    readonly Dictionary<Material, MatOriginal> matOriginals = new Dictionary<Material, MatOriginal>();
    readonly Dictionary<TerrainLayer, LayerOriginal> layerOriginals = new Dictionary<TerrainLayer, LayerOriginal>();
    float applied = -1f;
    static readonly int WetnessId = Shader.PropertyToID("_PZ_Wetness");
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");

    void OnEnable()
    {
        if (materials.Count == 0) Collect();
        foreach (var m in materials)
            if (m != null && m.HasProperty(BaseColorId) && !matOriginals.ContainsKey(m))
                matOriginals[m] = new MatOriginal { color = m.GetColor(BaseColorId), smoothness = m.HasProperty(SmoothnessId) ? m.GetFloat(SmoothnessId) : 0f };
        foreach (var l in terrainLayers)
            if (l != null && !layerOriginals.ContainsKey(l))
                layerOriginals[l] = new LayerOriginal { diffuseMax = l.diffuseRemapMax, smoothness = l.smoothness, maskMax = l.maskMapRemapMax };
        applied = -1f;
    }

    void OnDisable()
    {
        foreach (var kv in matOriginals)
        {
            if (kv.Key == null) continue;
            kv.Key.SetColor(BaseColorId, kv.Value.color);
            if (kv.Key.HasProperty(SmoothnessId)) kv.Key.SetFloat(SmoothnessId, kv.Value.smoothness);
        }
        foreach (var kv in layerOriginals)
        {
            if (kv.Key == null) continue;
            kv.Key.diffuseRemapMax = kv.Value.diffuseMax;
            kv.Key.smoothness = kv.Value.smoothness;
            kv.Key.maskMapRemapMax = kv.Value.maskMax;
        }
        matOriginals.Clear();
        layerOriginals.Clear();
        Shader.SetGlobalFloat(WetnessId, 0f);
    }

    // Outdoor materials in use in the scene, and the terrains' layers.
    public void Collect()
    {
        materials.Clear();
        var seen = new HashSet<Material>();
        foreach (var r in FindObjectsByType<Renderer>())
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || !seen.Add(m)) continue;
                foreach (var p in materialPrefixes)
                    if (m.name.StartsWith(p)) { materials.Add(m); break; }
            }
        terrainLayers.Clear();
        foreach (var t in FindObjectsByType<Terrain>())
            if (t.terrainData != null)
                foreach (var l in t.terrainData.terrainLayers)
                    if (l != null && !terrainLayers.Contains(l)) terrainLayers.Add(l);
    }

    void Update()
    {
        if (!Application.isPlaying) return;
        float target = Mathf.Clamp01(raining * 1.6f);
        float rate = target > wetness ? Time.deltaTime / Mathf.Max(1f, wetTime) * Mathf.Max(raining, 0.3f) : Time.deltaTime / Mathf.Max(1f, dryTime);
        wetness = Mathf.MoveTowards(wetness, target, rate);
        if (Mathf.Abs(wetness - applied) < 0.01f) return;
        applied = wetness;
        Shader.SetGlobalFloat(WetnessId, wetness);
        float k = Mathf.Lerp(1f, darken, wetness);
        foreach (var kv in matOriginals)
        {
            var m = kv.Key;
            if (m == null) continue;
            var c = kv.Value.color;
            m.SetColor(BaseColorId, new Color(c.r * k, c.g * k, c.b * k, c.a));
            // Materials with a smoothness map run _Smoothness at 1 already; the rest get glossier
            if (m.HasProperty(SmoothnessId) && kv.Value.smoothness < 0.99f)
                m.SetFloat(SmoothnessId, Mathf.Lerp(kv.Value.smoothness, Mathf.Max(kv.Value.smoothness, wetSmoothness), wetness));
        }
        foreach (var kv in layerOriginals)
        {
            var l = kv.Key;
            if (l == null) continue;
            var d = kv.Value.diffuseMax;
            l.diffuseRemapMax = new Vector4(d.x * k, d.y * k, d.z * k, d.w);
            l.smoothness = Mathf.Lerp(kv.Value.smoothness, Mathf.Max(kv.Value.smoothness, 0.6f), wetness);
            var mm = kv.Value.maskMax;
            l.maskMapRemapMax = new Vector4(mm.x, mm.y, mm.z, Mathf.Lerp(mm.w, Mathf.Min(1f, mm.w * 1.8f + 0.2f), wetness));
        }
    }
}
