using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Turns the built world into Pacific Northwest temperate rainforest: Douglas fir, western hemlock, western red cedar,
// Sitka spruce, big-leaf maple and red alder; sword fern, salal, Oregon grape, huckleberry, thimbleberry and moss
// underneath; fallen logs, stumps and mossy rocks; forest-floor, moss, meadow, dirt-trail and gravel ground; and a
// damp, overcast, foggy atmosphere.
//
// Run Tools > Environment > Build PNW Forest after Build Forest Field and Build World (re-run it whenever those are
// re-run). It works on both terrains in place: it reads where they are forest / meadow / road from their existing
// paint and the World/Roads meshes, then replaces their layers, trees and details.
//
// Assets (all CC0) live in Assets/Environment/PNW: textures and models from Poly Haven, leaf atlases from ambientCG.
// The foliage cards and decimated models there are made by Blender/Scripts/pnw_textures.py and pnw_models.py; the
// trees and shrubs themselves are generated here from those cards and bark textures.
//
// Plants use the ProjectZed/WindLit shader, so they move with the wind set by the scene's Weather/WindManager.
public static class PNWForestBuilder
{
    const string Dir = "Assets/Environment/PNW";
    const string GenDir = Dir + "/Generated";
    const int Seed = 2026;

    const float FieldRadius = 48f, ForestStart = 58f; // ForestBuilder's open field
    const float TileHalf = 160f;
    const float TreeSpacing = 6f;                      // jittered grid; ~250 stems/ha in full forest (incl. saplings)

    [MenuItem("Tools/Environment/Build PNW Forest")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) { Debug.LogError("[PNW] Exit Play mode first."); return; }
        var tileGo = GameObject.Find("/ForestTerrain");
        var worldGo = GameObject.Find("World/WorldTerrain");
        if (tileGo == null || worldGo == null) { Debug.LogError("[PNW] Run Build Forest Field and Build World first."); return; }

        AssetDatabase.Refresh();
        EnsureFolder(GenDir + "/Meshes");
        EnsureFolder(GenDir + "/Prefabs");
        EnsureFolder(GenDir + "/Materials");
        ConfigureImports();
        noise = new Vector2(431.7f, 912.3f);

        var tile = tileGo.GetComponent<Terrain>();
        var world = worldGo.GetComponent<Terrain>();
        roads = ReadRoads();
        clearings = ReadClearings();
        trails = BuildTrails(world, new System.Random(Seed + 7));

        var layers = BuildLayers();
        var trees = BuildTreePrototypes();
        var details = BuildDetailPrototypes();
        var props = LoadProps();

        var rng = new System.Random(Seed);
        var root = GameObject.Find("PNW_Props");
        if (root != null) Object.DestroyImmediate(root);
        root = new GameObject("PNW_Props");

        foreach (var (t, isTile) in new[] { (tile, true), (world, false) })
        {
            var data = t.terrainData;
            var cover = Paint(t, layers, isTile);
            PlaceTrees(t, trees, cover, isTile, rng);
            PlaceDetails(t, details, cover, isTile, rng);
            PlaceProps(t, props, cover, isTile, rng, root.transform);
            AddMossCollars(t, cover, root.transform);
            t.materialTemplate = ForestBuilder.TerrainMaterial(layers);
            t.treeDistance = 175f; // the fog has swallowed everything by ~170 m
            t.treeBillboardDistance = 175f;
            t.detailObjectDistance = 70f;
            t.detailObjectDensity = 1f;
            EditorUtility.SetDirty(data);
        }
        ReplaceFieldRocks(props, root.transform, tile);
        DressRoads();
        SetupAtmosphere();
        SetupWeather();

        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[PNW] Built PNW forest: {tile.terrainData.treeInstanceCount + world.terrainData.treeInstanceCount} trees, {root.transform.childCount} props, {trails.Count} trails.");
    }

    // ---------- Imports ----------
    static void ConfigureImports()
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Texture2D", new[] { Dir }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) continue;
            string file = Path.GetFileNameWithoutExtension(path);
            bool normal = file.EndsWith("_normal"), linear = normal || file.EndsWith("_mask") || file.EndsWith("_arm") || file.EndsWith("_rough");
            bool card = path.Contains("/Foliage/") || file == "fern_02_color" || file == "moss_01_color";
            bool sky = path.Contains("/Sky/");
            var type = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (ti.textureType == type && ti.sRGBTexture == !linear && ti.alphaIsTransparency == card && ti.mipMapsPreserveCoverage == card) continue;
            ti.textureType = type;
            ti.sRGBTexture = !linear;
            ti.alphaIsTransparency = card;
            ti.mipMapsPreserveCoverage = card;    // keeps alpha-tested foliage from thinning out with distance
            ti.alphaTestReferenceValue = 0.45f;
            ti.wrapMode = card ? TextureWrapMode.Clamp : TextureWrapMode.Repeat;
            ti.anisoLevel = card ? 1 : 4;
            ti.mipmapEnabled = !sky;
            ti.maxTextureSize = sky ? 4096 : 2048;
            if (sky) ti.textureCompression = TextureImporterCompression.CompressedHQ;
            ti.SaveAndReimport();
        }
        foreach (var guid in AssetDatabase.FindAssets("t:Model", new[] { Dir + "/Models" }))
        {
            var mi = (ModelImporter)AssetImporter.GetAtPath(AssetDatabase.GUIDToAssetPath(guid));
            if (mi.bakeAxisConversion && mi.materialImportMode == ModelImporterMaterialImportMode.None) continue;
            mi.bakeAxisConversion = true;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.importAnimation = false;
            mi.isReadable = true;
            mi.SaveAndReimport();
        }
    }

    static Texture2D Tex(string path) => AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    static string T(string set, string map) => $"{Dir}/Textures/{set}_{map}";

    // ---------- Materials ----------
    // Plants use ProjectZed/WindLit (URP Lit + GPU wind, driven by WindManager); logs and rocks plain URP Lit.
    static Material Mat(string name, bool wind = true)
    {
        string path = $"{GenDir}/Materials/{name}.mat";
        var shader = Shader.Find(wind ? "ProjectZed/WindLit" : "Universal Render Pipeline/Lit");
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(shader); AssetDatabase.CreateAsset(m, path); }
        if (m.shader != shader) m.shader = shader;
        m.enableInstancing = true; // terrain draws trees and details instanced
        return m;
    }

    // Wind response, in the mesh's own units (the shader scales it with the instance's height scale).
    // mode: "_WIND_TREE" (bend with height, flutter toward branch tips) or "_WIND_PLANT" (bend with height).
    static void SetWind(Material m, string mode, float bendHeight, float sway, float flutter, float flutterRadius, float swaySpeed, float flutterSpeed)
    {
        foreach (var k in new[] { "_WIND_TREE", "_WIND_PLANT", "_WIND_CLOTH" }) SetKeyword(m, k, k == mode);
        m.SetVector("_WindParams", new Vector4(bendHeight, sway, flutter, flutterRadius));
        m.SetVector("_WindParams2", new Vector4(swaySpeed, flutterSpeed, 0f, 0f));
        EditorUtility.SetDirty(m);
    }

    // A tiled PBR surface: colour, OpenGL normal, packed mask (R metal, G occlusion, A smoothness).
    static Material Surface(string name, string set, Color tint, float smoothness = 1f)
    {
        var m = Mat(name);
        m.SetTexture("_BaseMap", Tex(T(set, "color.jpg")));
        m.SetColor("_BaseColor", tint);
        SetMaps(m, Tex(T(set, "normal.jpg")), Tex(T(set, "mask.png")), smoothness);
        EditorUtility.SetDirty(m);
        return m;
    }

    static void SetMaps(Material m, Texture2D normal, Texture2D mask, float smoothness)
    {
        m.SetTexture("_BumpMap", normal);
        m.SetFloat("_BumpScale", 1f);
        SetKeyword(m, "_NORMALMAP", normal != null);
        m.SetTexture("_MetallicGlossMap", mask);
        m.SetTexture("_OcclusionMap", mask);
        m.SetFloat("_SmoothnessTextureChannel", 0f);
        m.SetFloat("_Smoothness", mask != null ? smoothness : 0.1f);
        m.SetFloat("_Metallic", 0f);
        SetKeyword(m, "_METALLICSPECGLOSSMAP", mask != null);
        SetKeyword(m, "_OCCLUSIONMAP", mask != null);
    }

    // Alpha-tested, two-sided card (leaves, needles, fern fronds).
    static Material Foliage(string name, Texture2D tex, Color tint, Texture2D normal = null)
    {
        var m = Mat(name);
        m.SetTexture("_BaseMap", tex);
        m.SetColor("_BaseColor", tint);
        m.SetFloat("_AlphaClip", 1f);
        m.SetFloat("_Cutoff", 0.45f);
        m.EnableKeyword("_ALPHATEST_ON");
        m.SetFloat("_Cull", (float)CullMode.Off);
        m.SetFloat("_Smoothness", 0.18f);
        m.SetFloat("_Metallic", 0f);
        m.SetTexture("_BumpMap", normal);
        SetKeyword(m, "_NORMALMAP", normal != null);
        m.SetOverrideTag("RenderType", "TransparentCutout");
        m.renderQueue = (int)RenderQueue.AlphaTest;
        m.doubleSidedGI = true;
        EditorUtility.SetDirty(m);
        return m;
    }

    static void SetKeyword(Material m, string k, bool on) { if (on) m.EnableKeyword(k); else m.DisableKeyword(k); }

    // ---------- Terrain layers ----------
    // 0 meadow grass, 1 dirt (trails, yards), 2 needle duff, 3 moss, 4 mossy rock, 5 gravel, 6 meadow thatch,
    // 7 dark needle litter. 0-2 keep the meaning of ForestBuilder's grass / dirt / forest floor.
    // Meadow and forest floor each have two textures mixed by large-scale noise: it breaks up the tiling grid you see
    // across open ground, and gives the colour variation (lush / dry patches, fresh / old litter) real ground has.
    // Eight layers is still two terrain passes, same as six.
    const int LMeadow = 0, LDirt = 1, LDuff = 2, LMoss = 3, LRock = 4, LGravel = 5, LMeadow2 = 6, LDuff2 = 7;

    static TerrainLayer[] BuildLayers()
    {
        return new[]
        {
            Layer("Meadow", "leafy_grass", 3.5f, new Color(0.82f, 0.9f, 0.78f)),
            Layer("Dirt", "mud_forest", 3f, Color.white),
            Layer("NeedleDuff", "forest_leaves_04", 2.5f, new Color(0.62f, 0.6f, 0.52f)),
            Layer("MossFloor", "moss003", 2.2f, new Color(0.66f, 0.74f, 0.58f)),
            Layer("MossyRock", "mossy_rock", 5f, Color.white),
            Layer("Gravel", "gravel_road", 3f, new Color(0.85f, 0.85f, 0.85f)),
            Layer("MeadowThatch", "grass004", 4.3f, new Color(0.85f, 0.85f, 0.75f)),
            Layer("NeedleLitter", "forest_leaves_03", 3.1f, new Color(0.8f, 0.76f, 0.7f)),
        };
    }

    static TerrainLayer Layer(string name, string set, float tile, Color tint)
    {
        string path = $"{GenDir}/Layer_{name}.terrainlayer";
        var l = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
        if (l == null) { l = new TerrainLayer(); AssetDatabase.CreateAsset(l, path); }
        l.diffuseTexture = Tex(T(set, "color.jpg"));
        l.normalMapTexture = Tex(T(set, "normal.jpg"));
        l.maskMapTexture = Tex(T(set, "mask.png"));
        l.normalScale = 1f;
        l.tileSize = new Vector2(tile, tile);
        l.diffuseRemapMax = tint;
        l.smoothness = 1f; // scales the mask map's smoothness
        l.metallic = 0f;
        EditorUtility.SetDirty(l);
        return l;
    }

    // ---------- Roads, clearings and trails ----------
    class Segments
    {
        public readonly List<Vector2> a = new List<Vector2>(), b = new List<Vector2>();
        public readonly List<float> halfWidth = new List<float>();
        public readonly List<bool> paved = new List<bool>();
        readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        const float Cell = 16f;

        public int Count => a.Count;

        public void Add(Vector2 p, Vector2 q, float hw, bool isPaved)
        {
            int i = a.Count;
            a.Add(p); b.Add(q); halfWidth.Add(hw); paved.Add(isPaved);
            float r = hw + 12f;
            int x0 = Mathf.FloorToInt((Mathf.Min(p.x, q.x) - r) / Cell), x1 = Mathf.FloorToInt((Mathf.Max(p.x, q.x) + r) / Cell);
            int z0 = Mathf.FloorToInt((Mathf.Min(p.y, q.y) - r) / Cell), z1 = Mathf.FloorToInt((Mathf.Max(p.y, q.y) + r) / Cell);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    long k = ((long)x << 32) ^ (uint)z;
                    if (!cells.TryGetValue(k, out var list)) cells[k] = list = new List<int>();
                    list.Add(i);
                }
        }

        // Distance from p to the nearest segment's edge (negative inside it); +inf if none within ~12 m.
        public float EdgeDistance(Vector2 p, out int seg)
        {
            seg = -1;
            long k = ((long)Mathf.FloorToInt(p.x / Cell) << 32) ^ (uint)Mathf.FloorToInt(p.y / Cell);
            if (!cells.TryGetValue(k, out var list)) return float.PositiveInfinity;
            float best = float.PositiveInfinity;
            foreach (int i in list)
            {
                Vector2 ab = b[i] - a[i];
                float t = Mathf.Clamp01(Vector2.Dot(p - a[i], ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                float d = Vector2.Distance(p, a[i] + ab * t) - halfWidth[i];
                if (d < best) { best = d; seg = i; }
            }
            return best;
        }
    }

    static Segments roads, trails;
    static List<(Vector2 centre, float radius)> clearings;
    static Vector2 noise;

    // Centre lines back out of the road meshes WorldBuilder made (vertex pairs = left / right edge).
    static Segments ReadRoads()
    {
        var s = new Segments();
        var parent = GameObject.Find("World/Roads");
        if (parent == null) return s;
        foreach (Transform r in parent.transform)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var v = mf.sharedMesh.vertices;
            bool paved = r.GetComponent<MeshRenderer>().sharedMaterial.name.Contains("Asphalt");
            for (int i = 0; i + 3 < v.Length; i += 2)
            {
                Vector3 c0 = r.TransformPoint((v[i] + v[i + 1]) * 0.5f), c1 = r.TransformPoint((v[i + 2] + v[i + 3]) * 0.5f);
                s.Add(new Vector2(c0.x, c0.z), new Vector2(c1.x, c1.z), Vector3.Distance(v[i], v[i + 1]) * 0.5f, paved);
            }
        }
        return s;
    }

    static List<(Vector2, float)> ReadClearings()
    {
        var list = new List<(Vector2, float)>();
        var parent = GameObject.Find("World/Locations");
        if (parent == null) return list;
        foreach (Transform site in parent.transform)
        {
            var c = new Vector2(site.position.x, site.position.z);
            float r = 25f;
            foreach (Transform child in site)
                r = Mathf.Max(r, Vector2.Distance(c, new Vector2(child.position.x, child.position.z)) + 14f);
            list.Add((c, r));
        }
        return list;
    }

    static float ClearingDistance(Vector2 p)
    {
        float best = float.PositiveInfinity;
        foreach (var (c, r) in clearings) best = Mathf.Min(best, Vector2.Distance(p, c) - r);
        return best;
    }

    // Foot trails wandering out into the forest from the field and from each location, ending at a road or the map edge.
    static Segments BuildTrails(Terrain world, System.Random rng)
    {
        var s = new Segments();
        var starts = new List<(Vector2 p, float heading)>();
        for (int i = 0; i < 7; i++)
        {
            float a = i * Mathf.PI * 2f / 7f + (float)rng.NextDouble() * 0.5f;
            starts.Add((new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (FieldRadius + 4f), a));
        }
        foreach (var (c, r) in clearings)
            for (int i = 0; i < 2; i++)
            {
                float a = (float)rng.NextDouble() * Mathf.PI * 2f;
                starts.Add((c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (r - 6f), a));
            }
        float half = world.terrainData.size.x * 0.5f - 40f;
        int n = 0;
        foreach (var (start, heading0) in starts)
        {
            Vector2 p = start;
            float heading = heading0, length = 220f + (float)rng.NextDouble() * 260f;
            float seed = n++ * 37.1f;
            for (float walked = 0f; walked < length; walked += 3f)
            {
                heading += (Mathf.PerlinNoise(seed, walked * 0.012f) - 0.5f) * 0.35f;
                var q = p + new Vector2(Mathf.Cos(heading), Mathf.Sin(heading)) * 3f;
                if (Mathf.Abs(q.x) > half || Mathf.Abs(q.y) > half) break;
                s.Add(p, q, 0.8f, false);
                p = q;
                if (walked > 30f && roads.EdgeDistance(p, out _) < 0f) break;
            }
        }
        return s;
    }

    // ---------- Cover ----------
    // Per alphamap texel of a terrain: how much forest, how mossy / wet, and what the ground there is.
    class Cover
    {
        public int res;
        public float size;
        public Vector3 origin;
        public float[,] forest, moss, open; // open: 1 on roads / trails / yards (no plants)
        public float[,,] alpha;

        public Vector2 World(int x, int z) => new Vector2(origin.x + (x + 0.5f) * size / res, origin.z + (z + 0.5f) * size / res);

        public float Sample(float[,] m, Vector2 p)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt((p.x - origin.x) / size * res), 0, res - 1);
            int z = Mathf.Clamp(Mathf.FloorToInt((p.y - origin.z) / size * res), 0, res - 1);
            return m[z, x];
        }
    }

    static float Smooth(float a, float b, float x) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, x));

    // PNW lowland is mostly forest: the world's woodland patches are widened, keeping the open field, the locations
    // and a few meadows clear.
    static float ForestCover(Vector2 p, float painted, bool isTile)
    {
        float f = painted;
        if (!isTile && Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y)) > TileHalf + 4f)
        {
            float extra = Smooth(0.36f, 0.48f, Mathf.PerlinNoise(noise.x + p.x * 0.0035f, noise.y + p.y * 0.0035f));
            f = Mathf.Max(f, extra);
        }
        if (isTile) f *= Smooth(FieldRadius, ForestStart + 2f, p.magnitude + (Mathf.PerlinNoise(p.x * 0.05f, p.y * 0.05f) - 0.5f) * 10f);
        f *= Smooth(0f, 25f, ClearingDistance(p));
        return Mathf.Clamp01(f);
    }

    static Cover Paint(Terrain t, TerrainLayer[] layers, bool isTile)
    {
        var data = t.terrainData;
        int ar = data.alphamapResolution;
        var old = data.GetAlphamaps(0, 0, ar, ar);
        int oldLayers = old.GetLength(2);
        var c = new Cover { res = ar, size = data.size.x, origin = t.transform.position };
        c.forest = new float[ar, ar]; c.moss = new float[ar, ar]; c.open = new float[ar, ar];
        var alpha = new float[ar, ar, layers.Length];
        var w = new float[layers.Length];
        for (int z = 0; z < ar; z++)
            for (int x = 0; x < ar; x++)
            {
                Vector2 p = c.World(x, z);
                // Forest share of the vegetated ground (forest floor vs meadow), so re-running reads back what it painted:
                // after a previous run the forest floor is split into duff and moss, and roads / rock take a share.
                float floor = (oldLayers > 2 ? old[z, x, 2] : 0f) + (oldLayers > 3 ? old[z, x, LMoss] : 0f) + (oldLayers > 7 ? old[z, x, LDuff2] : 0f);
                float meadow = old[z, x, 0] + (oldLayers > 6 ? old[z, x, LMeadow2] : 0f);
                float oldForest = floor + meadow > 0.05f ? floor / (floor + meadow) : 0.5f, oldDirt = oldLayers > 1 ? old[z, x, 1] : 0f;
                float dRoad = roads.EdgeDistance(p, out int seg);
                bool paved = seg >= 0 && roads.paved[seg];
                float dTrail = trails.EdgeDistance(p, out _);
                float slope = data.GetSteepness((x + 0.5f) / ar, (z + 0.5f) / ar);

                // Road beds and shoulders: gravel (tracks are gravel throughout), a rough dirt verge beyond.
                float gravel = 1f - Smooth(paved ? 1.5f : 1.0f, paved ? 3.5f : 3f, dRoad);
                float verge = (1f - Smooth(2.5f, 6f, dRoad)) * (1f - gravel);
                // Dirt that isn't road: yards, the field's worn patches, and foot trails.
                float yard = dRoad > 6f ? oldDirt : 0f;
                float trail = 1f - Smooth(0f, 1.2f, dTrail);
                float dirt = Mathf.Clamp01(Mathf.Max(Mathf.Max(yard, trail), verge * 0.7f));
                float bare = Mathf.Clamp01(gravel + dirt);

                float forest = ForestCover(p, oldForest, isTile);
                // Moss where it's damp: big patches, more in hollows and on the forest floor's shady side.
                float wet = Mathf.PerlinNoise(noise.x + 50f + p.x * 0.02f, noise.y + p.y * 0.02f) * 0.7f
                            + Mathf.PerlinNoise(noise.x + 90f + p.x * 0.09f, noise.y + p.y * 0.09f) * 0.3f;
                float moss = Smooth(0.3f, 0.5f, wet);
                float rock = Smooth(26f, 38f, slope);

                for (int l = 0; l < w.Length; l++) w[l] = 0f;
                w[LGravel] = gravel;
                w[LDirt] = dirt * (1f - gravel);
                float rest = Mathf.Max(0f, 1f - w[LGravel] - w[LDirt]);
                w[LRock] = rest * rock;
                rest -= w[LRock];
                w[LMoss] = rest * forest * moss;
                // Fresh orange-brown duff vs dark old litter, in drifts of tens of metres with finer breakup.
                float litter = Smooth(0.35f, 0.65f, Mathf.PerlinNoise(noise.x + 700f + p.x * 0.011f, noise.y + p.y * 0.011f) * 0.7f
                                                  + Mathf.PerlinNoise(noise.x + 730f + p.x * 0.07f, noise.y + p.y * 0.07f) * 0.3f);
                w[LDuff] = rest * forest * (1f - moss) * (1f - litter);
                w[LDuff2] = rest * forest * (1f - moss) * litter;
                // Meadow: green sward vs thatchy, drier ground (the same "dryness" field the grass types follow).
                float dry = Dryness(p);
                w[LMeadow] = rest * (1f - forest) * (1f - dry);
                w[LMeadow2] = rest * (1f - forest) * dry;
                float sum = 0f;
                for (int l = 0; l < w.Length; l++) sum += w[l];
                for (int l = 0; l < w.Length; l++) alpha[z, x, l] = sum > 0f ? w[l] / sum : (l == LMeadow ? 1f : 0f);

                c.forest[z, x] = forest;
                c.moss[z, x] = moss;
                c.open[z, x] = Mathf.Max(bare, Mathf.Max(1f - Smooth(-1f, 1.5f, dRoad), 1f - Smooth(-0.3f, 0.8f, dTrail)));
            }
        data.terrainLayers = layers;
        data.SetAlphamaps(0, 0, alpha);
        c.alpha = alpha;
        return c;
    }

    // ---------- Mesh building ----------
    class MeshBuilder
    {
        public readonly List<Vector3> v = new List<Vector3>(), n = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        public readonly List<int>[] subs;

        public MeshBuilder(int subMeshes)
        {
            subs = new List<int>[subMeshes];
            for (int i = 0; i < subMeshes; i++) subs[i] = new List<int>();
        }

        public int Vert(Vector3 p, Vector3 normal, Vector2 t) { v.Add(p); n.Add(normal); uv.Add(t); return v.Count - 1; }
        public void Quad(int sub, int a, int b, int c, int d) { subs[sub].AddRange(new[] { a, b, c, a, c, d }); }

        public Mesh Save(string name)
        {
            string path = $"{GenDir}/Meshes/{name}.asset";
            var m = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (m == null) { m = new Mesh(); AssetDatabase.CreateAsset(m, path); }
            m.Clear();
            m.name = name;
            m.indexFormat = v.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            m.SetVertices(v);
            m.SetNormals(n);
            m.SetUVs(0, uv);
            m.subMeshCount = subs.Length;
            for (int i = 0; i < subs.Length; i++) m.SetTriangles(subs[i], i);
            m.RecalculateBounds();
            m.RecalculateTangents();
            EditorUtility.SetDirty(m);
            return m;
        }
    }

    static float Rand(System.Random r, float a, float b) => a + (float)r.NextDouble() * (b - a);

    // A tapering tube along a path (trunks, limbs). UV: u around (uTiles repeats), v along (one repeat per vLen metres).
    // subAbove / splitY: segments starting above splitY go to another submesh (the shaded in-crown bark).
    static void Tube(MeshBuilder mb, int sub, IList<Vector3> path, IList<float> radii, int sides, float uTiles, float vLen, int subAbove = -1, float splitY = float.MaxValue)
    {
        int ring0 = mb.v.Count;
        float along = 0f;
        Vector3 refUp = Vector3.forward;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) along += Vector3.Distance(path[i], path[i - 1]);
            Vector3 dir = (path[Mathf.Min(i + 1, path.Count - 1)] - path[Mathf.Max(i - 1, 0)]).normalized;
            if (Mathf.Abs(Vector3.Dot(dir, refUp)) > 0.9f) refUp = Vector3.right;
            Vector3 side = Vector3.Cross(dir, refUp).normalized, fwd = Vector3.Cross(side, dir);
            for (int s = 0; s <= sides; s++)
            {
                float a = s * Mathf.PI * 2f / sides;
                Vector3 nrm = side * Mathf.Cos(a) + fwd * Mathf.Sin(a);
                mb.Vert(path[i] + nrm * radii[i], nrm, new Vector2(s / (float)sides * uTiles, along / vLen));
            }
        }
        for (int i = 0; i < path.Count - 1; i++)
            for (int s = 0; s < sides; s++)
            {
                int a = ring0 + i * (sides + 1) + s, b = a + sides + 1;
                mb.Quad(subAbove >= 0 && path[i].y >= splitY ? subAbove : sub, a, b, b + 1, a + 1);
            }
    }

    // Foliage normals point out of the crown (and a little up) rather than off the card, so two-sided cards light the
    // same from both sides and the crown shades like a volume.
    static Vector3 CrownNormal(Vector3 p, Vector3 centre)
    {
        Vector3 d = p - centre;
        return (d.normalized * 0.75f + Vector3.up * 0.55f).normalized;
    }

    // A conifer branch spray: a card bent in two segments (base on the trunk, tip out at `length`), base at u = 0.
    static void Frond(MeshBuilder mb, int sub, Vector3 origin, float yaw, float length, float width, float droop1, float droop2, float roll, Vector3 centre)
    {
        Vector3 h = new Vector3(Mathf.Cos(yaw), 0f, Mathf.Sin(yaw));
        Vector3 d1 = Quaternion.AngleAxis(droop1, Vector3.Cross(Vector3.up, h)) * h;
        Vector3 d2 = Quaternion.AngleAxis(droop2, Vector3.Cross(Vector3.up, h)) * h;
        Vector3 side = Quaternion.AngleAxis(roll, h) * Vector3.Cross(h, Vector3.up).normalized;
        Vector3 p1 = origin + d1 * length * 0.5f, p2 = p1 + d2 * length * 0.5f;
        Vector3[] spine = { origin, p1, p2 };
        int first = mb.v.Count;
        for (int i = 0; i < 3; i++)
        {
            float half = width * 0.5f * (i == 0 ? 0.6f : 1f);
            Vector3 l = spine[i] - side * half, r = spine[i] + side * half;
            mb.Vert(l, CrownNormal(l, centre), new Vector2(i * 0.5f, 0f));
            mb.Vert(r, CrownNormal(r, centre), new Vector2(i * 0.5f, 1f));
        }
        mb.Quad(sub, first, first + 2, first + 3, first + 1);
        mb.Quad(sub, first + 2, first + 4, first + 5, first + 3);
    }

    // A leafy cluster card: base at the card's bottom centre, growing along `up`.
    // upNormal 0..1 bends the normals toward straight up: grass and ground cover lit like the ground they grow from
    // (a standard trick, so a meadow reads as one surface instead of thousands of separately shaded cards).
    static void ClusterCard(MeshBuilder mb, int sub, Vector3 basePos, Vector3 up, Vector3 side, float size, Vector3 centre, float upNormal = 0f)
    {
        Vector3 a = basePos - side * size * 0.5f, b = basePos + side * size * 0.5f, c = b + up * size, d = a + up * size;
        System.Func<Vector3, Vector3> n = q => Vector3.Lerp(CrownNormal(q, centre), Vector3.up, upNormal).normalized;
        int i = mb.Vert(a, n(a), new Vector2(0, 0));
        mb.Vert(b, n(b), new Vector2(1, 0));
        mb.Vert(c, n(c), new Vector2(1, 1));
        mb.Vert(d, n(d), new Vector2(0, 1));
        mb.Quad(sub, i, i + 1, i + 2, i + 3);
    }

    // ---------- Trees ----------
    class Species
    {
        public string name, bark, card;
        public Color barkTint = Color.white, leafTint = Color.white;
        public float height, radius, flare, crownBase, crownRadius, whorl, droop1, droop2, cardWidth = 1f;
        public int perWhorl = 5;
        public bool broadleaf;
    }

    // Heights are a game-scale take on mature second-growth (real old growth runs 50-80 m).
    static readonly Species[] SpeciesList =
    {
        new Species { name = "DouglasFir", bark = "bark_brown_02", card = "Card_DouglasFir", height = 46f, radius = 0.85f, flare = 0.25f,
            crownBase = 0.4f, crownRadius = 6.5f, whorl = 0.6f, perWhorl = 5, droop1 = 8f, droop2 = -6f, leafTint = new Color(0.82f, 0.9f, 0.8f) },
        new Species { name = "WesternHemlock", bark = "tree_bark_03", card = "Card_Hemlock", height = 34f, radius = 0.6f, flare = 0.25f,
            crownBase = 0.25f, crownRadius = 5.2f, whorl = 0.5f, perWhorl = 6, droop1 = 18f, droop2 = 28f, barkTint = new Color(0.8f, 0.72f, 0.65f) },
        new Species { name = "WesternRedCedar", bark = "japanese_cedar_bark", card = "Card_RedCedar", height = 33f, radius = 0.95f, flare = 0.9f,
            crownBase = 0.2f, crownRadius = 6f, whorl = 0.55f, perWhorl = 5, droop1 = 30f, droop2 = -18f, cardWidth = 1.1f, barkTint = new Color(0.95f, 0.8f, 0.72f) },
        new Species { name = "SitkaSpruce", bark = "pine_bark", card = "Card_SitkaSpruce", height = 42f, radius = 0.9f, flare = 0.45f,
            crownBase = 0.3f, crownRadius = 5.8f, whorl = 0.5f, perWhorl = 6, droop1 = 6f, droop2 = 4f, cardWidth = 0.95f, barkTint = new Color(0.75f, 0.72f, 0.7f) },
        new Species { name = "BigleafMaple", bark = "trident_maple_bark", card = "Card_BigleafMaple", height = 20f, radius = 0.5f, flare = 0.3f,
            crownRadius = 8f, broadleaf = true, barkTint = new Color(0.7f, 0.78f, 0.62f), leafTint = new Color(0.72f, 0.78f, 0.66f) },
        new Species { name = "RedAlder", bark = "japanese_sycamore", card = "Card_RedAlder", height = 22f, radius = 0.3f, flare = 0.1f,
            crownRadius = 4f, broadleaf = true, leafTint = new Color(0.74f, 0.8f, 0.7f) },
    };
    const int DouglasFir = 0, Hemlock = 1, Cedar = 2, Spruce = 3, Maple = 4, Alder = 5;

    // Each species is built several times with different proportions, so neighbouring trees don't repeat.
    // Prototype layout: species s variant v at s * Variants + v, then the snags, then the stumps.
    const int Variants = 4, Snags = 2;
    static int TreeProto(int species, int variant) => species * Variants + variant;
    static int SnagProto(int i) => SpeciesList.Length * Variants + i;
    static int StumpProto(int i) => SpeciesList.Length * Variants + Snags + i;

    // How one individual differs from the species template.
    class Shape
    {
        public float crownScale = 1f, crownBaseShift, droop, lumpAmp, lumpFreq, lumpPhase, asym, asymDir, gaps;
        public float lean, leanDir, bow;   // trunk: lean (fraction of height), direction, sideways S-bend (m)
        public float top = 1f;             // fraction of the height that still has a live crown
        public bool spikeTop;              // cedar: dead bare spike above the crown
        public bool brokenTop;             // Douglas fir / spruce: snapped top, a couple of branches turned up as new leaders
    }

    static Shape MakeShape(Species sp, int variant, System.Random rng)
    {
        var s = new Shape
        {
            crownScale = Rand(rng, 0.82f, 1.18f),
            crownBaseShift = Rand(rng, -0.08f, 0.08f),
            droop = Rand(rng, -6f, 6f),
            lumpAmp = Rand(rng, 0.04f, 0.13f),     // crowns bulge and pinch where branches died or thrived
            lumpFreq = Rand(rng, 9f, 18f),
            lumpPhase = Rand(rng, 0f, 6.28f),
            asym = Rand(rng, 0f, 0.22f),            // one-sided from crowding by neighbours
            asymDir = Rand(rng, 0f, 6.28f),
            gaps = Rand(rng, 0.04f, 0.22f),
            lean = Rand(rng, 0f, 0.035f),
            leanDir = Rand(rng, 0f, 6.28f),
            bow = Rand(rng, 0f, 0.5f),
        };
        // Variant 3 is the old one: an irregular crown and (for most species) a damaged top.
        if (variant == 3)
        {
            s.gaps += 0.1f; s.lumpAmp += 0.05f; s.asym += 0.08f;
            if (sp == SpeciesList[Cedar]) { s.spikeTop = true; s.top = Rand(rng, 0.82f, 0.88f); }
            else if (sp == SpeciesList[DouglasFir] || sp == SpeciesList[Spruce]) { s.brokenTop = true; s.top = Rand(rng, 0.8f, 0.88f); }
        }
        if (variant == 1) { s.crownScale *= 0.85f; s.crownBaseShift += 0.06f; } // narrow, high-crowned (grew up crowded)
        if (variant == 2) { s.crownScale *= 1.12f; s.crownBaseShift -= 0.06f; } // full, low-crowned (grew up in a gap)
        return s;
    }

    static TreePrototype[] BuildTreePrototypes()
    {
        var protos = new List<TreePrototype>();
        foreach (var sp in SpeciesList)
        {
            var bark = Surface("Bark_" + sp.name, sp.bark, sp.barkTint, 0.6f);
            var leaves = Foliage("Leaves_" + sp.name, Tex($"{Dir}/Foliage/{sp.card}.png"), sp.leafTint);
            // Big conifers sway slowly a metre or so at the top in a storm; broadleaves move more and flutter more.
            // Bark and leaves share the bend, so branches stay attached to the trunk.
            float sway = sp.height * (sp.broadleaf ? 0.05f : 0.03f), speed = sp.broadleaf ? (sp == SpeciesList[Maple] ? 0.75f : 0.9f) : 0.55f;
            SetWind(bark, "_WIND_TREE", sp.height, sway, 0f, 1f, speed, 5f);
            // The stem inside the live crown sits in deep shade (foliage all around it blocks the sky), so it gets
            // the same bark much darker. Without this a pale trunk shows right up through the needles.
            var crownBark = Surface("BarkCrown_" + sp.name, sp.bark, sp.broadleaf ? sp.barkTint : sp.barkTint * new Color(0.38f, 0.36f, 0.34f, 1f), 0.3f);
            SetWind(crownBark, "_WIND_TREE", sp.height, sway, 0f, 1f, speed, 5f);
            SetWind(leaves, "_WIND_TREE", sp.height, sway, sp.broadleaf ? 0.16f : 0.09f, sp.crownRadius * 0.5f, speed, sp.broadleaf ? 6f : 4.5f);
            for (int v = 0; v < Variants; v++)
            {
                int seed = Seed + StableHash(sp.name) % 10000 + v * 7919;
                var lods = new Mesh[2];
                for (int lod = 0; lod < 2; lod++)
                {
                    // Same seed and the same sequence of random numbers at both LODs, so the far version is the same
                    // tree with fewer cards, not a different tree (which is what made them pop when switching).
                    var rng = new System.Random(seed);
                    var shape = MakeShape(sp, v, rng);
                    var mb = new MeshBuilder(3); // bark, foliage, in-crown bark
                    if (sp.broadleaf) BuildBroadleaf(mb, sp, shape, rng, lod); else BuildConifer(mb, sp, shape, rng, lod);
                    lods[lod] = mb.Save($"{sp.name}_{v + 1}_LOD{lod}");
                }
                protos.Add(new TreePrototype { prefab = TreePrefab($"{sp.name}_{v + 1}", lods, new[] { bark, leaves, crownBark }, sp.radius * 0.8f, sp.height) });
            }
        }
        // Snags: standing dead Douglas firs, grey and broken off, a common sight in PNW forest.
        var snagBark = Surface("Bark_Snag", "bark_brown_02", new Color(0.68f, 0.66f, 0.63f), 0.4f);
        SetWind(snagBark, "_WIND_TREE", 30f, 0.15f, 0f, 1f, 0.5f, 5f);
        for (int i = 0; i < Snags; i++)
        {
            var lods = new Mesh[2];
            for (int lod = 0; lod < 2; lod++)
            {
                var mb = new MeshBuilder(2);
                BuildSnag(mb, new System.Random(Seed + 31 + i), lod, i == 0 ? 0.55f : 0.35f);
                lods[lod] = mb.Save($"Snag_{i + 1}_LOD{lod}");
            }
            protos.Add(new TreePrototype { prefab = TreePrefab($"Snag_{i + 1}", lods, new[] { snagBark, snagBark }, 0.7f, 46f * (i == 0 ? 0.55f : 0.35f)) });
        }
        // Stumps (Poly Haven) as trees: upright, capsule collider, placed with the forest.
        protos.Add(new TreePrototype { prefab = PropPrefab("Stump_1", "tree_stump_01", PropMaterial("tree_stump_01"), 1.6f, false, true) });
        protos.Add(new TreePrototype { prefab = PropPrefab("Stump_2", "tree_stump_02", PropMaterial("tree_stump_02"), 1.8f, false, true) });
        return protos.ToArray();
    }

    // string.GetHashCode isn't guaranteed stable between runs; this is.
    static int StableHash(string s)
    {
        int h = 17;
        foreach (char ch in s) h = h * 31 + ch;
        return h & 0x7fffffff;
    }

    static float TrunkRadius(Species sp, float y)
    {
        float t = Mathf.Clamp01(y / sp.height);
        // Tapers faster than linear up the crown (the stem is thin where only the top's foliage depends on it).
        return sp.radius * Mathf.Pow(1f - t, 1.2f) + 0.025f + sp.radius * sp.flare * Mathf.Exp(-Mathf.Max(0f, y) / 1.2f);
    }

    // Where the (leaning, slightly bowed) trunk axis is at height y.
    static Vector3 Axis(Species sp, Shape s, float y)
    {
        float t = Mathf.Clamp01(y / sp.height);
        var lean = new Vector3(Mathf.Cos(s.leanDir), 0f, Mathf.Sin(s.leanDir));
        var side = new Vector3(-lean.z, 0f, lean.x);
        return new Vector3(0f, y, 0f) + lean * (s.lean * sp.height * t * t) + side * (s.bow * Mathf.Sin(t * Mathf.PI * 1.5f));
    }

    static void BuildConifer(MeshBuilder mb, Species sp, Shape s, System.Random rng, int lod)
    {
        bool hemlock = sp == SpeciesList[Hemlock];
        float liveTop = sp.height * s.top;
        // The trunk stops inside the leader; only a cedar's dead spike pokes out above the green.
        float trunkTop = s.spikeTop ? sp.height * Mathf.Min(1f, s.top + 0.12f) : liveTop - (s.brokenTop ? 0.2f : 1.2f);

        float crownBase = sp.height * Mathf.Clamp(sp.crownBase + s.crownBaseShift, 0.1f, 0.6f);

        // Trunk: lit bark below the crown, shaded bark (submesh 2) from just above the lowest whorl up.
        int rings = lod == 0 ? 14 : 7, sides = lod == 0 ? 10 : 6;
        var path = new List<Vector3>();
        var radii = new List<float>();
        for (int i = 0; i <= rings; i++)
        {
            float t = i / (float)rings;
            float y = -0.6f + Mathf.Pow(t, 1.6f) * (trunkTop + 0.6f); // closer rings low down, for the flare
            path.Add(Axis(sp, s, y));
            radii.Add(TrunkRadius(sp, y) + (s.brokenTop && i == rings ? 0.15f : 0f));
        }
        Tube(mb, 0, path, radii, sides, Mathf.Max(2f, Mathf.Round(Mathf.PI * 2f * sp.radius / 1.8f)), 1.8f, 2, crownBase + 1f);

        // Dead lower branches: short grey stubs below the crown (Douglas fir and spruce self-prune but keep these).
        int stubs = sp == SpeciesList[Cedar] ? 4 : 10;
        for (int i = 0; i < stubs; i++)
        {
            float y = Rand(rng, 4f, crownBase), az = Rand(rng, 0f, 6.28f), len = Rand(rng, 0.4f, 1.6f);
            if (lod != 0) continue; // tiny at a distance; the random numbers are still drawn so both LODs stay in step
            var dir = new Vector3(Mathf.Cos(az), Rand(rng, -0.3f, 0.1f), Mathf.Sin(az)).normalized;
            var o = Axis(sp, s, y);
            Tube(mb, 0, new[] { o, o + dir * len }, new[] { 0.07f, 0.02f }, 4, 1f, 1.5f);
        }

        // Branch sprays, whorl by whorl up the crown: a cone, widest at its base, narrowing to the leader, with the
        // individual's bulges, one-sidedness and gaps.
        float y0 = crownBase, y1 = liveTop - 1.2f;
        float golden = 2.39996f, yaw = Rand(rng, 0f, 6.28f);
        int whorl = 0;
        for (float y = y0; y < y1; y += sp.whorl * Rand(rng, 0.85f, 1.15f), whorl++)
        {
            float t = (y - y0) / (sp.height - y0);
            float lumps = 1f + s.lumpAmp * Mathf.Sin(t * s.lumpFreq + s.lumpPhase);
            float crown = (sp.crownRadius * s.crownScale * Mathf.Pow(1f - t, 0.9f) * Mathf.Lerp(0.7f, 1f, Smooth(0f, 0.12f, t)) + 0.4f) * lumps;
            var axis = Axis(sp, s, y);
            Vector3 centre = axis - new Vector3(0f, crown * 0.3f, 0f);
            int count = Mathf.Max(3, sp.perWhorl - (t > 0.8f ? 2 : 0));
            for (int b = 0; b < count; b++)
            {
                yaw += golden;
                float len = crown * Rand(rng, 0.8f, 1.1f) * (1f + s.asym * Mathf.Cos(yaw - s.asymDir));
                bool gap = rng.NextDouble() < s.gaps * (1f - t);
                float d1 = (sp.droop1 + s.droop) * Mathf.Lerp(1f, 0.4f, t) + Rand(rng, -5f, 5f), d2 = sp.droop2 + Rand(rng, -6f, 6f);
                float roll1 = Rand(rng, 15f, 40f), yaw2 = yaw + Rand(rng, -0.25f, 0.25f), roll2 = -Rand(rng, 45f, 70f);
                if (gap) continue;
                // Start the card a little behind the stem: the first ~12% of a spray texture is bare twig, and with
                // the card starting at the bark that left a needle-free tube around the trunk all the way up.
                float back = -len * 0.12f;
                Vector3 origin = axis + new Vector3(Mathf.Cos(yaw) * back, 0f, Mathf.Sin(yaw) * back);
                // The spray fills only the middle ~65% of its card, so cards are wider than the branch is long.
                float width = len * sp.cardWidth * 1.4f;
                if (lod == 0)
                {
                    Frond(mb, 1, origin, yaw, len, width, d1, d2, roll1, centre);
                    if (t < 0.92f) Frond(mb, 1, origin, yaw2, len * 0.9f, width, d1 + 4f, d2, roll2, centre);
                }
                // Far LOD: every other branch (alternating per whorl, so the same branches in the same places) as
                // one bigger card set between the near LOD's two.
                else if ((b + whorl) % 2 == 0) Frond(mb, 1, origin, yaw, len * 1.08f, width * 1.8f, d1 + 2f, d2, (roll1 + 40f) * 0.5f, centre);
            }
        }

        // Crown core: upright sprays crossed on the stem every couple of metres. Real crowns are densest near the
        // stem (short inner shoots, epicormic sprouts); these fill the middle so the stem doesn't show through from
        // any angle. No random numbers, so both LODs get the same cards.
        for (float y = y0 + 1f; y < y1 - 1f; y += 2.2f)
        {
            float t = (y - y0) / (sp.height - y0);
            float crown = sp.crownRadius * s.crownScale * Mathf.Pow(1f - t, 0.9f) + 0.4f;
            float w = Mathf.Clamp(crown * 0.9f, 1.2f, 3.5f) * (lod == 0 ? 1f : 1.4f); // the far LOD has half the sprays
            float az = y * 2.39996f;
            var o = Axis(sp, s, y - 0.4f);
            Frond(mb, 1, o, az, 3f, w, -86f, -88f, 0f, o);
            Frond(mb, 1, o, az + 1.5708f, 3f, w, -86f, -88f, 0f, o);
        }

        // The top.
        var top = Axis(sp, s, liveTop - 1.2f);
        if (s.brokenTop)
        {
            // Snapped off: two or three side branches have turned up to become new leaders.
            for (int i = 0; i < 3; i++)
            {
                float az = Rand(rng, 0f, 6.28f);
                var o = top + new Vector3(Mathf.Cos(az) * 0.3f, -Rand(rng, 0.5f, 2f), Mathf.Sin(az) * 0.3f);
                Leader(mb, sp, o, az, Rand(rng, 2.5f, 4f), false);
            }
        }
        else
        {
            // A spire of short sprays angled up, then the leader itself: upright, or for hemlock nodding over.
            for (int i = 0; i < 4; i++)
            {
                float az = yaw + i * 1.57f;
                var o = top + new Vector3(0f, i * 0.3f, 0f);
                Frond(mb, 1, o, az, 1.6f - i * 0.25f, 1.8f - i * 0.3f, -35f - i * 8f, -20f, 30f, top);
            }
            Leader(mb, sp, top + new Vector3(0f, 0.6f, 0f), yaw, 2.4f, hemlock);
        }
    }

    // A leader: two crossed sprays pointing up (the card's base at the bottom of the branch). Hemlock's droops over.
    static void Leader(MeshBuilder mb, Species sp, Vector3 at, float yaw, float length, bool nod)
    {
        float d1 = nod ? -55f : -82f, d2 = nod ? 70f : -80f;
        Frond(mb, 1, at, yaw, length, length * 0.6f, d1, d2, 0f, at);
        Frond(mb, 1, at, yaw + 1.5708f, length, length * 0.6f, d1, d2, 0f, at);
    }

    // A dead, grey, broken-off fir: bare trunk with short stubs.
    static void BuildSnag(MeshBuilder mb, System.Random rng, int lod, float heightFraction)
    {
        var sp = SpeciesList[DouglasFir];
        float h = sp.height * heightFraction;
        var path = new List<Vector3>();
        var radii = new List<float>();
        int rings = lod == 0 ? 8 : 4;
        for (int i = 0; i <= rings; i++)
        {
            float y = -0.6f + (h + 0.6f) * i / rings;
            path.Add(new Vector3(0f, y, 0f));
            radii.Add(TrunkRadius(sp, y) * (i == rings ? 1.15f : 1f)); // splintered, not tapered, at the break
        }
        Tube(mb, 0, path, radii, lod == 0 ? 9 : 6, 3f, 1.8f);
        int stubs = 18;
        for (int i = 0; i < stubs; i++)
        {
            float y = Rand(rng, h * 0.3f, h - 0.5f), az = Rand(rng, 0f, 6.28f), len = Rand(rng, 0.5f, 2.2f);
            var dir = new Vector3(Mathf.Cos(az), Rand(rng, -0.4f, 0.2f), Mathf.Sin(az)).normalized;
            if (lod != 0 && i % 2 == 1) continue;
            var o = new Vector3(0f, y, 0f);
            Tube(mb, 0, new[] { o, o + dir * len }, new[] { 0.09f, 0.025f }, 4, 1f, 1.5f);
        }
    }

    static void BuildBroadleaf(MeshBuilder mb, Species sp, Shape s, System.Random rng, int lod)
    {
        bool maple = sp == SpeciesList[Maple];
        int sides = lod == 0 ? 8 : 5;
        Vector3 crownCentre = new Vector3(0f, sp.height * (maple ? 0.62f : 0.68f), 0f);
        // Trunk: maple forks low (higher or lower per tree) into big spreading limbs; alder runs straight up.
        float trunkTop = sp.height * (maple ? 0.28f + s.crownBaseShift : 0.88f);
        var path = new List<Vector3>();
        var radii = new List<float>();
        for (int i = 0; i <= 6; i++)
        {
            float y = -0.5f + (trunkTop + 0.5f) * i / 6f;
            path.Add(Axis(sp, s, y));
            radii.Add(TrunkRadius(sp, y) * (maple ? 1f : 1f - 0.5f * i / 6f));
        }
        float uTiles = Mathf.Max(2f, Mathf.Round(Mathf.PI * 2f * sp.radius / 1.5f));
        Tube(mb, 0, path, radii, sides, uTiles, 1.5f);
        Vector3 top = path[path.Count - 1];

        if (maple)
        {
            int limbs = 3 + rng.Next(3);
            for (int i = 0; i < limbs; i++)
            {
                float az = i * Mathf.PI * 2f / limbs + Rand(rng, -0.4f, 0.4f);
                Vector3 dir = (new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az)) * Rand(rng, 0.55f, 0.95f) * (1f + s.asym * Mathf.Cos(az - s.asymDir)) + Vector3.up).normalized;
                Limb(mb, sp, rng, top, dir, Rand(rng, 7f, 11f) * s.crownScale, sp.radius * 0.55f, 0, 2, sides, crownCentre, lod);
            }
        }
        else
        {
            for (float y = sp.height * (0.4f + s.crownBaseShift); y < trunkTop; y += 1.1f)
            {
                float az = Rand(rng, 0f, 6.28f);
                float t = (y - sp.height * 0.4f) / (trunkTop - sp.height * 0.4f);
                Vector3 dir = (new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az)) + Vector3.up * Rand(rng, 0.5f, 1.1f)).normalized;
                float len = sp.crownRadius * s.crownScale * Mathf.Lerp(1f, 0.5f, t) * Rand(rng, 0.8f, 1.1f) * (1f + s.asym * Mathf.Cos(az - s.asymDir));
                if (rng.NextDouble() < s.gaps) continue;
                Limb(mb, sp, rng, Axis(sp, s, y), dir, len, 0.08f, 1, 2, lod == 0 ? 5 : 4, crownCentre, lod);
            }
            Leaves(mb, sp, rng, top, Vector3.up, 2.6f, crownCentre, lod);
        }
    }

    static void Limb(MeshBuilder mb, Species sp, System.Random rng, Vector3 start, Vector3 dir, float length, float radius, int depth, int maxDepth, int sides, Vector3 centre, int lod)
    {
        var path = new List<Vector3>();
        var radii = new List<float>();
        Vector3 p = start, d = dir;
        Vector3 bend = new Vector3(Rand(rng, -1f, 1f), Rand(rng, -0.3f, 0.6f), Rand(rng, -1f, 1f)) * 0.15f;
        for (int i = 0; i <= 4; i++)
        {
            path.Add(p);
            radii.Add(radius * (1f - 0.6f * i / 4f));
            d = (d + bend).normalized;
            p += d * length / 4f;
        }
        Tube(mb, 0, path, radii, Mathf.Max(4, sides - depth * 2), 1f, 1.5f);
        if (depth < maxDepth)
        {
            int kids = depth == 0 ? 3 : 2;
            for (int k = 0; k < kids; k++)
            {
                int at = 2 + k % 3;
                Vector3 kd = (d + new Vector3(Rand(rng, -0.9f, 0.9f), Rand(rng, -0.1f, 0.5f), Rand(rng, -0.9f, 0.9f))).normalized;
                Limb(mb, sp, rng, path[Mathf.Min(at, 4)], kd, length * Rand(rng, 0.5f, 0.7f), radii[Mathf.Min(at, 4)] * 0.8f, depth + 1, maxDepth, sides, centre, lod);
            }
        }
        else
        {
            for (int i = 2; i <= 4; i++)
                Leaves(mb, sp, rng, path[i], d, sp == SpeciesList[Maple] ? 3.4f : 2.4f, centre, lod, emit: lod == 0 || i != 3);
        }
    }

    // A tuft of crossed cluster cards around a twig end. Random numbers are always drawn for all three cards so the
    // LODs stay identical; the far LOD just skips some.
    static void Leaves(MeshBuilder mb, Species sp, System.Random rng, Vector3 at, Vector3 dir, float size, Vector3 centre, int lod, bool emit = true)
    {
        const int cards = 3;
        Vector3 outward = (dir + (at - centre).normalized * 0.6f + Vector3.up * 0.4f).normalized;
        for (int i = 0; i < cards; i++)
        {
            Vector3 up = Quaternion.AngleAxis(Rand(rng, -35f, 35f), Random3(rng)) * outward;
            Vector3 across = Vector3.Cross(up, Mathf.Abs(up.y) > 0.95f ? Vector3.right : Vector3.up).normalized;
            Vector3 side = Quaternion.AngleAxis(i * 180f / cards + Rand(rng, -15f, 15f), up) * across;
            float scale = Rand(rng, 0.85f, 1.15f);
            if (!emit || (lod != 0 && i == 2)) continue;
            ClusterCard(mb, 1, at - up * size * 0.3f, up, side, size * scale * (lod == 0 ? 1f : 1.2f), centre);
        }
    }

    static Vector3 Random3(System.Random rng) => new Vector3(Rand(rng, -1f, 1f), Rand(rng, -1f, 1f), Rand(rng, -1f, 1f)).normalized + Vector3.one * 1e-4f;

    static GameObject TreePrefab(string name, Mesh[] lods, Material[] materials, float colliderRadius, float height)
    {
        var go = new GameObject(name);
        var renderers = new LOD[lods.Length];
        for (int i = 0; i < lods.Length; i++)
        {
            var child = new GameObject("LOD" + i);
            child.transform.SetParent(go.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = lods[i];
            var mr = child.AddComponent<MeshRenderer>();
            mr.sharedMaterials = materials;
            renderers[i] = new LOD(i == 0 ? 0.5f : 0.008f, new Renderer[] { mr });
        }
        var group = go.AddComponent<LODGroup>();
        group.SetLODs(renderers);
        group.fadeMode = LODFadeMode.CrossFade; // dithered blend between LODs instead of a hard switch
        group.animateCrossFading = true;
        group.RecalculateBounds();
        var col = go.AddComponent<CapsuleCollider>();
        col.radius = colliderRadius;
        col.height = height * 0.6f;
        col.center = new Vector3(0f, height * 0.3f, 0f);
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{GenDir}/Prefabs/Tree_{name}.prefab");
        Object.DestroyImmediate(go);
        return prefab;
    }

    // ---------- Models (Poly Haven) ----------
    static Material PropMaterial(string model, Color? tint = null, bool cutout = false, string materialName = null)
    {
        string baseDir = Dir + "/Models/" + model;
        var color = Tex(baseDir + "_color.jpg") ?? Tex(baseDir + "_color.png");
        if (cutout) return Foliage(materialName ?? "Prop_" + model, color, tint ?? Color.white, Tex(baseDir + "_normal.jpg"));
        var m = Mat("Prop_" + model, wind: false);
        m.SetTexture("_BaseMap", color);
        m.SetColor("_BaseColor", tint ?? Color.white);
        SetMaps(m, Tex(baseDir + "_normal.jpg"), null, 0.1f);
        m.SetFloat("_Smoothness", 0.12f);
        EditorUtility.SetDirty(m);
        return m;
    }

    // Every mesh in a model file, re-pivoted to its base centre (models in a "set" sit side by side in the file).
    static List<Mesh> ModelMeshes(string model)
    {
        var result = new List<Mesh>();
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath($"{Dir}/Models/{model}.fbx"))
        {
            if (!(o is Mesh src)) continue;
            string path = $"{GenDir}/Meshes/{model}_{src.name}.asset";
            var m = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (m == null) { m = new Mesh(); AssetDatabase.CreateAsset(m, path); }
            m.Clear();
            // The FBX meshes come in Z-down with a +90 X rotation on their node; bake that in so the meshes stand up.
            var v = src.vertices;
            var nrm = src.normals;
            for (int i = 0; i < v.Length; i++) { v[i] = new Vector3(v[i].x, -v[i].z, v[i].y); nrm[i] = new Vector3(nrm[i].x, -nrm[i].z, nrm[i].y); }
            var b = GeometryUtility.CalculateBounds(v, Matrix4x4.identity);
            var offset = new Vector3(b.center.x, b.min.y, b.center.z);
            for (int i = 0; i < v.Length; i++) v[i] -= offset;
            m.name = $"{model}_{src.name}"; // matches the asset file name
            m.indexFormat = src.indexFormat;
            m.SetVertices(v);
            m.SetNormals(nrm);
            m.SetUVs(0, src.uv);
            m.SetTriangles(src.triangles, 0);
            m.RecalculateBounds();
            m.RecalculateTangents();
            EditorUtility.SetDirty(m);
            result.Add(m);
        }
        result.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
        return result;
    }

    // A prefab from a model's first mesh scaled so its largest dimension is `size` metres.
    static GameObject PropPrefab(string name, string model, Material mat, float size, bool meshCollider, bool capsule, int meshIndex = 0)
    {
        var meshes = ModelMeshes(model);
        var mesh = meshes[Mathf.Min(meshIndex, meshes.Count - 1)];
        var go = new GameObject(name);
        var body = new GameObject("Mesh");
        body.transform.SetParent(go.transform, false);
        var e = mesh.bounds.size;
        body.transform.localScale = Vector3.one * (size / Mathf.Max(e.x, Mathf.Max(e.y, e.z)));
        body.AddComponent<MeshFilter>().sharedMesh = mesh;
        body.AddComponent<MeshRenderer>().sharedMaterial = mat;
        if (meshCollider) body.AddComponent<MeshCollider>().sharedMesh = mesh;
        if (capsule)
        {
            // Terrain trees need their renderer reachable from the root to be instanced: a one-level LODGroup does it.
            var lod = go.AddComponent<LODGroup>();
            lod.SetLODs(new[] { new LOD(0.01f, new Renderer[] { body.GetComponent<MeshRenderer>() }) });
            var s = Vector3.Scale(mesh.bounds.size, body.transform.localScale);
            var col = go.AddComponent<CapsuleCollider>();
            col.radius = Mathf.Min(s.x, s.z) * 0.4f;
            col.height = s.y;
            col.center = new Vector3(0f, s.y * 0.5f, 0f);
        }
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{GenDir}/Prefabs/{name}.prefab");
        Object.DestroyImmediate(go);
        return prefab;
    }

    // ---------- Undergrowth ----------
    class Plant
    {
        public string name;
        public float perM2;        // in full forest
        public float edge;         // extra density toward forest edges / openings (0..1 of perM2)
        public float meadow;       // density factor out in the open
        public bool mossOnly;
        public float clump;        // patchiness: 0 even, 1 tight patches
        public System.Func<Vector2, float, float, float> density; // (position, forest, moss) -> plants per m2, overrides the above
        public int index;
    }

    static readonly List<Plant> Plants = new List<Plant>();

    static DetailPrototype[] BuildDetailPrototypes()
    {
        Plants.Clear();
        var list = new List<DetailPrototype>();
        // Meadow bunchgrass (cards composed from photographed blades by pnw_ground.py). PNW lowland meadows are
        // fescue / oatgrass / hairgrass bunchgrass: lush where it's wet, going to seed and straw where it's dry, so the
        // four tuft types follow the same wet/dry field as the meadow ground textures.
        var grasses = new (string name, string card, float height, Color tint, System.Func<float, float, float> share)[]
        {
            // Tints above 1 lift the composed cards, which come out dark (photo blades with darkened bases).
            ("GrassLush", "Card_GrassLush", 0.5f, new Color(1.55f, 1.6f, 1.3f), (dry, wet) => (1f - dry) * (0.7f + 0.6f * wet)),
            ("GrassMixed", "Card_GrassMixed", 0.48f, new Color(1.5f, 1.5f, 1.3f), (dry, wet) => 1f),
            ("GrassSeeding", "Card_GrassSeeding", 0.62f, new Color(1.05f, 1.02f, 0.92f), (dry, wet) => Smooth(0.45f, 0.9f, dry) * 0.7f),
            // Poly Haven's clump impostors are darker and much more solid than the composed tufts: lifted more, used less.
            ("GrassClumps", "Card_GrassClumps", 0.45f, new Color(1.9f, 1.9f, 1.6f), (dry, wet) => 0.3f),
        };
        foreach (var g in grasses)
        {
            var mat = Foliage("Grass_" + g.name, Tex($"{Dir}/Foliage/{g.card}.png"), g.tint);
            SetWind(mat, "_WIND_PLANT", g.height, g.height * 0.22f, 0.025f, 1f, 1.8f, 8f);
            var share = g.share;
            AddDetail(list, DetailPrefab(g.name, TuftMesh(g.name, g.height, new System.Random(g.name.Length * 17)), mat), 0.75f, 1.3f,
                new Plant { name = g.name, density = (p, forest, moss) => GrassDensity(p, forest) * share(Dryness(p), moss) * 0.6f });
        }

        // Forest-floor herbs: Oregon oxalis carpets the moss in damp shade; vanilla leaf grows in big patches; deer
        // fern (smaller, darker, ladder-like) in the wettest spots.
        var oxalisMat = Foliage("Ground_Oxalis", Tex($"{Dir}/Foliage/Card_Oxalis.png"), new Color(0.68f, 0.76f, 0.64f));
        SetWind(oxalisMat, "_WIND_PLANT", 0.2f, 0.01f, 0.01f, 1f, 1.5f, 6f);
        AddDetail(list, DetailPrefab("Oxalis", GroundCoverMesh("Oxalis", 0.55f, 0.1f, 4, new System.Random(3)), oxalisMat), 0.8f, 1.3f,
            new Plant { name = "Oxalis", density = (p, forest, moss) => 0.55f * forest * moss * Patch(p, 51f, 0.05f) });
        var vanillaMat = Foliage("Ground_VanillaLeaf", Tex($"{Dir}/Foliage/Card_VanillaLeaf.png"), new Color(0.66f, 0.74f, 0.6f));
        SetWind(vanillaMat, "_WIND_PLANT", 0.35f, 0.03f, 0.02f, 1f, 1.5f, 6f);
        AddDetail(list, DetailPrefab("VanillaLeaf", GroundCoverMesh("VanillaLeaf", 0.6f, 0.28f, 3, new System.Random(4)), vanillaMat), 0.8f, 1.25f,
            new Plant { name = "VanillaLeaf", density = (p, forest, moss) => 0.22f * forest * Patch(p, 77f, 0.035f) });

        // Sword fern: Poly Haven fern_02, its four fronds-clumps as separate variants.
        var fernMat = PropMaterial("fern_02", new Color(0.72f, 0.82f, 0.68f), cutout: true);
        SetWind(fernMat, "_WIND_PLANT", 0.3f, 0.06f, 0.02f, 1f, 1.6f, 7f);
        var ferns = ModelMeshes("fern_02");
        for (int i = 0; i < ferns.Count; i++)
        {
            var prefab = DetailPrefab($"SwordFern_{i + 1}", ferns[i], fernMat);
            float size = Mathf.Max(ferns[i].bounds.size.x, ferns[i].bounds.size.z);
            float s = 1.3f / Mathf.Max(0.1f, size); // ~1.3 m across, like a mature sword fern
            // The two big clumps are ~2,300 tris each, so they're kept rare; the small ones carry the density.
            AddDetail(list, prefab, s * 0.8f, s * 1.25f, new Plant { name = "SwordFern", perM2 = (ferns[i].triangles.Length < 6000 ? 0.11f : 0.012f), edge = 0.2f, meadow = 0.02f, clump = 0.6f });
        }
        // Deer fern: the small fern clumps again, darker and smaller, only where it's wet.
        var deerMat = PropMaterial("fern_02", new Color(0.5f, 0.62f, 0.48f), cutout: true, materialName: "Prop_fern_02_deer");
        SetWind(deerMat, "_WIND_PLANT", 0.3f, 0.05f, 0.02f, 1f, 1.6f, 7f);
        var smallFern = ferns.Find(f => f.triangles.Length < 6000) ?? ferns[0];
        float fernSize = 0.6f / Mathf.Max(0.1f, Mathf.Max(smallFern.bounds.size.x, smallFern.bounds.size.z));
        AddDetail(list, DetailPrefab("DeerFern", smallFern, deerMat), fernSize * 0.8f, fernSize * 1.2f,
            new Plant { name = "DeerFern", density = (p, forest, moss) => 0.06f * forest * Smooth(0.5f, 0.9f, moss) * Patch(p, 93f, 0.06f) });

        // Shrubs, built from the leaf cards.
        var shrubs = new (string name, string card, float height, int cards, float spread, Color tint, float perM2, float edge, float meadow, float clump)[]
        {
            ("Salal", "Card_Salal", 0.9f, 7, 40f, new Color(0.8f, 0.85f, 0.8f), 0.07f, 1.2f, 0.05f, 0.7f),
            ("OregonGrape", "Card_OregonGrape", 0.55f, 5, 30f, new Color(0.85f, 0.85f, 0.85f), 0.03f, 0.3f, 0f, 0.5f),
            ("RedHuckleberry", "Card_Huckleberry", 1.4f, 5, 25f, new Color(0.85f, 0.9f, 0.85f), 0.012f, 0.3f, 0f, 0.3f),
            ("Thimbleberry", "Card_Thimbleberry", 1.2f, 6, 30f, Color.white, 0.006f, 4f, 0.12f, 0.8f),
        };
        foreach (var sh in shrubs)
        {
            var mat = Foliage("Shrub_" + sh.name, Tex($"{Dir}/Foliage/{sh.card}.png"), sh.tint);
            SetWind(mat, "_WIND_PLANT", sh.height, sh.height * 0.12f, 0.04f, 1f, 1.3f, 6.5f);
            var mesh = BushMesh(sh.name, sh.height, sh.cards, sh.spread, new System.Random(sh.name.Length * 31));
            AddDetail(list, DetailPrefab(sh.name, mesh, mat), 0.8f, 1.3f,
                new Plant { name = sh.name, perM2 = sh.perM2, edge = sh.edge, meadow = sh.meadow, clump = sh.clump });
        }

        // Moss tufts (Poly Haven moss_01) carpeting the mossy ground, logs aside.
        var mossMat = PropMaterial("moss_01", new Color(0.85f, 0.95f, 0.8f), cutout: true);
        SetWind(mossMat, "_WIND_PLANT", 1f, 0f, 0f, 1f, 1f, 1f); // no sway (moss doesn't move), but it fades in like the rest
        var moss = ModelMeshes("moss_01");
        for (int i = 0; i < moss.Count; i += 3) // the clumps are only a few cm across in the file; grown into cushions
            AddDetail(list, DetailPrefab($"Moss_{i + 1}", moss[i], mossMat), 20f, 32f, new Plant { name = "Moss", perM2 = 0.4f, mossOnly = true, clump = 0.3f });
        return list.ToArray();
    }

    static void AddDetail(List<DetailPrototype> list, GameObject prefab, float minScale, float maxScale, Plant plant)
    {
        var d = new DetailPrototype
        {
            usePrototypeMesh = true,
            prototype = prefab,
            renderMode = DetailRenderMode.VertexLit,
            useInstancing = true,
            minWidth = minScale, maxWidth = maxScale,
            minHeight = minScale, maxHeight = maxScale,
            noiseSpread = 0.3f,
            healthyColor = Color.white, dryColor = Color.white,
        };
        if (!d.Validate(out string error)) Debug.LogWarning($"[PNW] {prefab.name}: {error}");
        plant.index = list.Count;
        Plants.Add(plant);
        list.Add(d);
    }

    static GameObject DetailPrefab(string name, Mesh mesh, Material mat)
    {
        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = ShadowCastingMode.Off; // undergrowth shadows cost a lot for little under an overcast sky
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{GenDir}/Prefabs/Detail_{name}.prefab");
        Object.DestroyImmediate(go);
        return prefab;
    }

    // A bush: cluster cards fanned around the centre, each leaning outward.
    static Mesh BushMesh(string name, float height, int cards, float spread, System.Random rng)
    {
        var mb = new MeshBuilder(1);
        var centre = new Vector3(0f, height * 0.25f, 0f);
        for (int i = 0; i < cards; i++)
        {
            float az = i * Mathf.PI * 2f / cards + Rand(rng, -0.3f, 0.3f);
            var outward = new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az));
            var up = Quaternion.AngleAxis(spread * Rand(rng, 0.6f, 1.2f), Vector3.Cross(Vector3.up, outward)) * Vector3.up;
            var side = Vector3.Cross(up, outward).normalized;
            ClusterCard(mb, 0, outward * 0.05f, up, side, height * Rand(rng, 0.85f, 1.15f), centre);
        }
        return mb.Save("Bush_" + name);
    }

    // ---------- Placement ----------
    static void PlaceTrees(Terrain t, TreePrototype[] protos, Cover c, bool isTile, System.Random rng)
    {
        var data = t.terrainData;
        data.treePrototypes = protos;
        float size = data.size.x;
        var origin = t.transform.position;
        var list = new List<TreeInstance>();
        for (float z = TreeSpacing * 0.5f; z < size; z += TreeSpacing)
            for (float x = TreeSpacing * 0.5f; x < size; x += TreeSpacing)
            {
                var p = new Vector2(origin.x + x + Rand(rng, -0.45f, 0.45f) * TreeSpacing, origin.z + z + Rand(rng, -0.45f, 0.45f) * TreeSpacing);
                if (!isTile && Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y)) < TileHalf + 2f) continue; // the field terrain's hole
                if (Mathf.Abs(p.x - origin.x - size * 0.5f) > size * 0.5f - 10f || Mathf.Abs(p.y - origin.z - size * 0.5f) > size * 0.5f - 10f) continue;
                float forest = c.Sample(c.forest, p);
                // Dense in the forest; the odd tree out in the meadows.
                if (rng.NextDouble() > Mathf.Lerp(0.012f, 0.92f, forest * forest)) continue;
                if (roads.EdgeDistance(p, out _) < 3.5f || trails.EdgeDistance(p, out _) < 1.5f || ClearingDistance(p) < 2f) continue;
                if (isTile && p.magnitude < FieldRadius + 3f) continue;

                float moss = c.Sample(c.moss, p);
                float edge = 1f - forest;
                float roadNear = 1f - Smooth(4f, 30f, roads.EdgeDistance(p, out _));
                int sp = PickSpecies(rng, moss, Mathf.Max(edge, roadNear));
                int proto = TreeProto(sp, rng.Next(Variants));
                float scale = Rand(rng, 0.75f, 1.2f);
                // Shade-tolerant understory: young hemlock and cedar fill in under the big trees.
                if ((sp == Hemlock || sp == Cedar) && rng.NextDouble() < 0.3) scale = Rand(rng, 0.12f, 0.4f);
                double dead = rng.NextDouble();
                if (dead < 0.025 * forest) { proto = StumpProto(rng.Next(2)); scale = Rand(rng, 0.8f, 1.6f); }
                else if (dead < 0.045 * forest) { proto = SnagProto(rng.Next(Snags)); scale = Rand(rng, 0.8f, 1.2f); }
                list.Add(new TreeInstance
                {
                    prototypeIndex = proto,
                    position = new Vector3((p.x - origin.x) / size, 0f, (p.y - origin.z) / size),
                    widthScale = scale * Rand(rng, 0.9f, 1.1f),
                    heightScale = scale,
                    rotation = Rand(rng, 0f, Mathf.PI * 2f),
                    color = Color.white,
                    lightmapColor = Color.white,
                });
            }
        data.SetTreeInstances(list.ToArray(), true);
    }

    // Douglas fir dominates, western hemlock next; cedar likes the wet spots, spruce is scattered (it's a coastal
    // tree), and alder and big-leaf maple take the edges, road verges and clearings.
    static int PickSpecies(System.Random rng, float wet, float edge)
    {
        float[] w =
        {
            0.40f * (1f - edge * 0.6f),
            0.25f * (1f - edge * 0.5f),
            0.13f * (0.5f + wet),
            0.07f,
            0.05f + 0.18f * edge,
            0.04f + 0.30f * edge,
        };
        float sum = 0f;
        foreach (var x in w) sum += x;
        float r = (float)rng.NextDouble() * sum;
        for (int i = 0; i < w.Length; i++) { r -= w[i]; if (r <= 0f) return i; }
        return 0;
    }

    static void PlaceDetails(Terrain t, DetailPrototype[] protos, Cover c, bool isTile, System.Random rng)
    {
        var data = t.terrainData;
        int dr = data.detailResolution;
        // Small patches (~16 m): the terrain draws or culls undergrowth a whole patch at a time, and with big ones it
        // was processing plants out to ~150 m (already faded to nothing by the shader) for no visible gain.
        int perPatch = Mathf.Clamp(Mathf.RoundToInt(16f / (data.size.x / data.detailResolution)), 8, 32);
        if (data.detailResolutionPerPatch != perPatch) data.SetDetailResolution(data.detailResolution, perPatch);
        data.detailPrototypes = protos;
        data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
        float cell = data.size.x / dr, area = cell * cell;
        var origin = t.transform.position;
        var layers = new int[protos.Length][,];
        for (int l = 0; l < protos.Length; l++) layers[l] = new int[dr, dr];
        for (int z = 0; z < dr; z++)
            for (int x = 0; x < dr; x++)
            {
                var p = new Vector2(origin.x + (x + 0.5f) * cell, origin.z + (z + 0.5f) * cell);
                if (!isTile && Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y)) < TileHalf) continue;
                float open = c.Sample(c.open, p);
                if (open > 0.4f) continue;
                float forest = c.Sample(c.forest, p), moss = c.Sample(c.moss, p);
                float edge = forest * (1f - forest) * 4f; // peaks at the forest margin
                foreach (var plant in Plants)
                {
                    float density;
                    if (plant.density != null) density = plant.density(p, forest, moss);
                    else if (plant.mossOnly) density = plant.perM2 * moss * forest;
                    else density = plant.perM2 * (forest * (1f + plant.edge * edge) + plant.meadow * (1f - forest));
                    float patch = Mathf.PerlinNoise(noise.x + plant.index * 31.3f + p.x * 0.06f, noise.y + p.y * 0.06f);
                    density *= Mathf.Lerp(1f, Smooth(0.35f, 0.7f, patch) * 2.2f, plant.clump);
                    float expected = density * area * (1f - open);
                    int n = Mathf.FloorToInt(expected);
                    if (rng.NextDouble() < expected - n) n++;
                    layers[plant.index][z, x] = Mathf.Min(n, 24);
                }
            }
        for (int l = 0; l < protos.Length; l++) data.SetDetailLayer(0, 0, l, layers[l]);
    }

    // Moss gathers where it stays damp and undisturbed: around the bases of big trees, on and beside stumps and fallen
    // logs. Paints the moss layer in soft rings / strips over what Paint laid down (forest only, never on roads).
    static void AddMossCollars(Terrain t, Cover c, Transform propsRoot)
    {
        var data = t.terrainData;
        int ar = c.res, layers = c.alpha.GetLength(2);
        float cell = c.size / ar;
        var origin = t.transform.position;
        void Ring(Vector2 p, float radius, float strength)
        {
            int cx = Mathf.FloorToInt((p.x - origin.x) / cell), cz = Mathf.FloorToInt((p.y - origin.z) / cell);
            int r = Mathf.CeilToInt(radius / cell) + 1;
            for (int z = Mathf.Max(0, cz - r); z <= Mathf.Min(ar - 1, cz + r); z++)
                for (int x = Mathf.Max(0, cx - r); x <= Mathf.Min(ar - 1, cx + r); x++)
                {
                    if (c.open[z, x] > 0.2f || c.forest[z, x] < 0.3f) continue;
                    var q = c.World(x, z);
                    float m = strength * (1f - Smooth(radius * 0.4f, radius, Vector2.Distance(p, q)));
                    if (m <= 0f) continue;
                    for (int l = 0; l < layers; l++) c.alpha[z, x, l] *= 1f - m;
                    c.alpha[z, x, LMoss] += m;
                }
        }
        foreach (var ti in data.treeInstances)
        {
            if (ti.heightScale < 0.5f) continue; // saplings don't have a moss skirt yet
            var p = new Vector2(origin.x + ti.position.x * c.size, origin.z + ti.position.z * c.size);
            Ring(p, 1.2f + 1.6f * ti.heightScale, 0.75f);
        }
        var props = propsRoot.Find("Props_" + t.name);
        if (props != null)
            foreach (Transform prop in props)
            {
                if (prop.name != "Log") continue;
                // A log is long: ring a few points along it.
                var mf = prop.GetComponent<MeshFilter>();
                float half = mf != null ? mf.sharedMesh.bounds.extents.x * prop.localScale.x : 4f;
                for (float k = -1f; k <= 1.01f; k += 0.5f)
                {
                    var w = prop.position + prop.right * half * k;
                    Ring(new Vector2(w.x, w.z), 2.2f, 0.65f);
                }
            }
        data.SetAlphamaps(0, 0, c.alpha);
    }

    // ---------- Props: fallen logs and mossy rocks ----------
    class Props
    {
        public List<(Mesh mesh, Material mat, float length)> logs = new List<(Mesh, Material, float)>();
        public List<(Mesh mesh, Material mat)> rocks = new List<(Mesh, Material)>();
        public Mesh boulder;
        public Material boulderMat;
    }

    static Props LoadProps()
    {
        var p = new Props();
        foreach (var model in new[] { "dead_tree_trunk", "dead_tree_trunk_02" })
        {
            var mat = PropMaterial(model, new Color(0.85f, 0.9f, 0.8f));
            foreach (var m in ModelMeshes(model)) p.logs.Add((m, mat, Mathf.Max(m.bounds.size.x, m.bounds.size.z)));
        }
        foreach (var model in new[] { "rock_moss_set_01", "rock_moss_set_02" })
        {
            var mat = PropMaterial(model);
            foreach (var m in ModelMeshes(model)) p.rocks.Add((m, mat));
        }
        p.boulder = ModelMeshes("boulder_01")[0];
        p.boulderMat = PropMaterial("boulder_01", new Color(0.8f, 0.85f, 0.78f));
        return p;
    }

    static GameObject Prop(Transform parent, string name, Mesh mesh, Material mat, Vector3 pos, Quaternion rot, float scale)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.SetPositionAndRotation(pos, rot);
        go.transform.localScale = Vector3.one * scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccludeeStatic);
        return go;
    }

    static void PlaceProps(Terrain t, Props props, Cover c, bool isTile, System.Random rng, Transform root)
    {
        var data = t.terrainData;
        var origin = t.transform.position;
        float size = data.size.x;
        var parent = new GameObject("Props_" + t.name).transform; // not the terrain's own name: Build looks terrains up by name
        parent.SetParent(root, false);
        // Fallen logs: ~3 per hectare of forest (old-growth floors are littered with them); mossy rocks ~2 per hectare.
        int attempts = Mathf.RoundToInt(size * size / 10000f * 6f);
        for (int i = 0; i < attempts; i++)
        {
            var p = new Vector2(origin.x + Rand(rng, 12f, size - 12f), origin.z + Rand(rng, 12f, size - 12f));
            if (!isTile && Mathf.Max(Mathf.Abs(p.x), Mathf.Abs(p.y)) < TileHalf + 4f) continue;
            if (isTile && p.magnitude < ForestStart) continue;
            float forest = c.Sample(c.forest, p);
            if (rng.NextDouble() > forest * forest) continue;
            if (roads.EdgeDistance(p, out _) < 8f || trails.EdgeDistance(p, out _) < 3f || ClearingDistance(p) < 5f) continue;
            float ground = t.SampleHeight(new Vector3(p.x, 0f, p.y)) + origin.y;
            Vector3 nrm = data.GetInterpolatedNormal((p.x - origin.x) / size, (p.y - origin.z) / size);
            var yaw = Quaternion.AngleAxis(Rand(rng, 0f, 360f), Vector3.up);
            var tilt = Quaternion.FromToRotation(Vector3.up, nrm);
            if (i % 2 == 0)
            {
                var log = props.logs[rng.Next(props.logs.Count)];
                float scale = Rand(rng, 9f, 16f) / log.length; // big old-growth logs, 9-16 m long
                Prop(parent, "Log", log.mesh, log.mat, new Vector3(p.x, ground - 0.2f * scale, p.y), tilt * yaw, scale);
            }
            else if (rng.NextDouble() < 0.7)
            {
                var rock = props.rocks[rng.Next(props.rocks.Count)];
                float s = Rand(rng, 0.8f, 2.4f) / Mathf.Max(0.1f, Mathf.Max(rock.mesh.bounds.size.x, rock.mesh.bounds.size.z));
                Prop(parent, "MossyRock", rock.mesh, rock.mat, new Vector3(p.x, ground - 0.15f * s, p.y), tilt * yaw, s);
            }
        }
    }

    // ForestBuilder's low-poly field rocks become mossy boulders (same spots, so the field keeps its cover).
    static void ReplaceFieldRocks(Props props, Transform root, Terrain tile)
    {
        GameObject rocks = null; // found even when a previous run switched it off
        foreach (var go in EditorSceneManager.GetActiveScene().GetRootGameObjects()) if (go.name == "Rocks") rocks = go;
        if (rocks == null) return;
        var parent = new GameObject("FieldRocks").transform;
        parent.SetParent(root, false);
        var rng = new System.Random(Seed + 3);
        foreach (Transform r in rocks.transform)
        {
            if (!r.gameObject.activeSelf) continue;
            var rend = r.GetComponentInChildren<Renderer>();
            float size = rend != null ? Mathf.Max(rend.bounds.size.x, rend.bounds.size.z) : 2f;
            bool boulder = r.position.magnitude < ForestStart;
            var mesh = boulder ? props.boulder : props.rocks[rng.Next(props.rocks.Count)].mesh;
            var mat = boulder ? props.boulderMat : props.rocks[0].mat;
            foreach (var pr in props.rocks) if (pr.mesh == mesh) mat = pr.mat;
            float s = size / Mathf.Max(0.1f, Mathf.Max(mesh.bounds.size.x, mesh.bounds.size.z));
            float ground = tile.SampleHeight(r.position) + tile.transform.position.y;
            Prop(parent, boulder ? "Boulder" : "MossyRock", mesh, mat, new Vector3(r.position.x, ground - 0.1f * s, r.position.z),
                Quaternion.Euler(0f, Rand(rng, 0f, 360f), 0f), s);
        }
        rocks.SetActive(false);
    }

    // ---------- Roads ----------
    static void DressRoads()
    {
        var dirt = AssetDatabase.LoadAssetAtPath<Material>("Assets/Environment/World/M_Road_Dirt.mat");
        if (dirt != null)
        {
            dirt.SetTexture("_BaseMap", Tex(T("gravel_road", "color.jpg")));
            dirt.SetTextureScale("_BaseMap", new Vector2(1f, 1f));
            dirt.SetColor("_BaseColor", new Color(0.85f, 0.82f, 0.78f));
            SetMaps(dirt, Tex(T("gravel_road", "normal.jpg")), Tex(T("gravel_road", "mask.png")), 0.8f);
            EditorUtility.SetDirty(dirt);
        }
        var asphalt = AssetDatabase.LoadAssetAtPath<Material>("Assets/Environment/World/M_Road_Asphalt.mat");
        if (asphalt != null)
        {
            asphalt.SetColor("_BaseColor", new Color(0.16f, 0.16f, 0.17f));
            asphalt.SetFloat("_Smoothness", 0.42f); // it's always just rained
            EditorUtility.SetDirty(asphalt);
        }
    }

    // A grass tuft: three cards crossed at 60 degrees, each leaning a little outward, normals mostly up.
    static Mesh TuftMesh(string name, float height, System.Random rng)
    {
        var mb = new MeshBuilder(1);
        var centre = new Vector3(0f, -height, 0f);
        for (int i = 0; i < 3; i++)
        {
            float az = i * Mathf.PI / 3f + Rand(rng, -0.2f, 0.2f);
            var side = new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az));
            var lean = Quaternion.AngleAxis(Rand(rng, -12f, 12f), side) * Vector3.up;
            ClusterCard(mb, 0, Vector3.down * 0.02f, lean, side, height, centre, 0.75f);
        }
        return mb.Save("Tuft_" + name);
    }

    // Low ground cover: top-down leaf cards laid nearly flat (tilted 10-25 degrees) at about `height`, overlapping.
    static Mesh GroundCoverMesh(string name, float size, float height, int cards, System.Random rng)
    {
        var mb = new MeshBuilder(1);
        for (int i = 0; i < cards; i++)
        {
            float az = i * Mathf.PI * 2f / cards + Rand(rng, -0.4f, 0.4f);
            var flat = new Vector3(Mathf.Cos(az), 0f, Mathf.Sin(az));
            var side = new Vector3(-flat.z, 0f, flat.x);
            var up = Quaternion.AngleAxis(-Rand(rng, 10f, 25f), side) * flat; // the card's "up" runs across the ground
            var centre = flat * Rand(rng, 0f, size * 0.25f) + Vector3.up * height * Rand(rng, 0.7f, 1.1f);
            ClusterCard(mb, 0, centre - up * size * 0.5f, up, side, size, centre + Vector3.down, 0.9f);
        }
        return mb.Save("Cover_" + name);
    }

    // 0 wet / lush .. 1 dry / thatchy: big soft patches plus finer breakup. Meadow ground textures and grass types both
    // follow it, so the grass and the ground under it agree.
    static float Dryness(Vector2 p) =>
        Smooth(0.3f, 0.7f, Mathf.PerlinNoise(noise.x + 610f + p.x * 0.008f, noise.y + p.y * 0.008f) * 0.65f
                         + Mathf.PerlinNoise(noise.x + 640f + p.x * 0.045f, noise.y + p.y * 0.045f) * 0.35f);

    // Meadow grass tufts per m2: thick in the open, thinning under the forest edge, with bare-ish pockets.
    static float GrassDensity(Vector2 p, float forest)
    {
        float pockets = Smooth(0.2f, 0.55f, Mathf.PerlinNoise(noise.x + 500f + p.x * 0.09f, noise.y + p.y * 0.09f));
        return 6f * Mathf.Clamp01(1.3f - forest * 1.7f) * (0.35f + 0.65f * pockets);
    }

    // 0..~2.2 patch multiplier: a plant growing in drifts (scale = 1 / patch size in metres).
    static float Patch(Vector2 p, float seed, float scale) =>
        Smooth(0.42f, 0.72f, Mathf.PerlinNoise(noise.x + seed + p.x * scale, noise.y + seed * 0.7f + p.y * scale)) * 2.2f;

    // The scene's "Weather" object: WindManager (+ a WindZone for particles). Kept if it's already there, so wind
    // settings made in its Inspector survive a rebuild.
    static void SetupWeather()
    {
        GameObject weather = null;
        foreach (var go in EditorSceneManager.GetActiveScene().GetRootGameObjects()) if (go.name == "Weather") weather = go;
        if (weather == null) weather = new GameObject("Weather");
        if (weather.GetComponent<WindZone>() == null) weather.AddComponent<WindZone>();
        if (weather.GetComponent<WindManager>() == null) weather.AddComponent<WindManager>();
    }

    // ---------- Atmosphere ----------
    static void SetupAtmosphere()
    {
        var skyPath = GenDir + "/Materials/Sky_Overcast.mat";
        var sky = AssetDatabase.LoadAssetAtPath<Material>(skyPath);
        if (sky == null) { sky = new Material(Shader.Find("Skybox/Panoramic")); AssetDatabase.CreateAsset(sky, skyPath); }
        sky.SetTexture("_MainTex", Tex(Dir + "/Sky/kloofendal_overcast_puresky_2k.hdr"));
        sky.SetFloat("_Exposure", 0.85f);
        sky.SetColor("_Tint", new Color(0.62f, 0.66f, 0.68f));
        EditorUtility.SetDirty(sky);
        RenderSettings.skybox = sky;

        // Flat, cool overcast light: a weak, soft sun high overhead and a strong grey-green ambient fill.
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.6f, 0.65f, 0.68f);
        RenderSettings.ambientEquatorColor = new Color(0.42f, 0.46f, 0.43f);
        RenderSettings.ambientGroundColor = new Color(0.18f, 0.2f, 0.16f);
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
        RenderSettings.reflectionIntensity = 0.6f;

        Light sun = null;
        foreach (var l in Object.FindObjectsByType<Light>())
            if (l.type == LightType.Directional) sun = l;
        if (sun != null)
        {
            sun.transform.rotation = Quaternion.Euler(58f, -30f, 0f);
            sun.color = new Color(0.86f, 0.9f, 0.95f);
            sun.intensity = 0.75f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.6f;
        }

        // Fog: the far side of the forest fades out at ~150 m.
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.011f;
        RenderSettings.fogColor = new Color(0.58f, 0.63f, 0.64f);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
