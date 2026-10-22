using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Builds the play area: a flat open field in the middle of a terrain, ringed by forested hills.
// Trees, rocks, ground textures and grass are all generated procedurally (low-poly style), so it needs
// no imported assets. Re-run from Tools > Environment > Build Forest Field; change Seed for a new layout.
public static class ForestBuilder
{
    const string Dir = "Assets/Environment/Forest";
    const int Seed = 1234;

    const float TerrainSize = 320f;     // metres, centred on the origin
    const float TerrainHeight = 40f;
    const float FlatRadius = 25f;       // perfectly level around the spawn and targets
    const float FieldRadius = 48f;      // open grass out to here
    const float ForestStart = 58f;      // trees from here outwards
    const float BoundaryInset = 8f;     // invisible walls this far in from the terrain edge

    [MenuItem("Tools/Environment/Build Forest Field")]
    public static void Build()
    {
        EnsureFolder(Dir);
        var rng = new System.Random(Seed);

        var layers = BuildTerrainLayers();
        var grassBlade = BuildGrassBladeTexture();
        var trees = BuildTreePrefabs(rng);
        var rockMeshes = BuildRockMeshes(rng);

        RemoveOld();
        var terrain = BuildTerrain(layers, grassBlade, trees, rng);
        PlaceRocks(terrain, rockMeshes, rng);
        BuildBoundary();
        PlaceGameplayObjects(terrain);
        SetupAtmosphere();

        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log("[ForestBuilder] Built forest field in " + scene.path);
    }

    // ---------- Scene cleanup ----------
    static void RemoveOld()
    {
        foreach (var name in new[] { "Room", "FillLight", "ForestTerrain", "Rocks", "Boundary" })
        {
            var go = GameObject.Find(name);
            if (go != null) Object.DestroyImmediate(go);
        }
    }

    // ---------- Terrain ----------
    static Terrain BuildTerrain(TerrainLayer[] layers, Texture2D grassBlade, GameObject[] trees, System.Random rng)
    {
        string dataPath = Dir + "/ForestTerrain.asset";
        AssetDatabase.DeleteAsset(dataPath);
        var data = new TerrainData { heightmapResolution = 257, alphamapResolution = 256 };
        data.size = new Vector3(TerrainSize, TerrainHeight, TerrainSize);
        data.SetDetailResolution(512, 16);
        AssetDatabase.CreateAsset(data, dataPath);

        // World y of the terrain's zero height. The field sits at y = 0, leaving room for dips below it.
        const float baseY = -2f;
        float fieldH = -baseY / TerrainHeight;

        int hr = data.heightmapResolution;
        var heights = new float[hr, hr];
        float ox = (float)rng.NextDouble() * 1000f, oz = (float)rng.NextDouble() * 1000f;
        for (int z = 0; z < hr; z++)
            for (int x = 0; x < hr; x++)
            {
                Vector2 p = ToWorld(x, z, hr);
                float d = p.magnitude;
                // Gentle undulation in the outer field, flat in the middle.
                float roll = (Mathf.PerlinNoise(ox + p.x * 0.03f, oz + p.y * 0.03f) - 0.5f) * 1.2f
                             * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(FlatRadius, FieldRadius, d));
                // Hills rise through the forest so the field feels enclosed.
                float hills = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(FieldRadius, TerrainSize * 0.5f, d))
                              * (8f + Mathf.PerlinNoise(ox + p.x * 0.015f, oz + p.y * 0.015f) * 16f);
                float bumps = (Mathf.PerlinNoise(ox + p.x * 0.12f, oz + p.y * 0.12f) - 0.5f) * 0.6f
                              * Mathf.InverseLerp(FieldRadius, ForestStart + 20f, d);
                heights[z, x] = Mathf.Clamp01(fieldH + (roll + hills + bumps) / TerrainHeight);
            }
        data.SetHeights(0, 0, heights);

        // Splat: grass field, forest floor under the trees, worn dirt patches in between.
        data.terrainLayers = layers;
        int ar = data.alphamapResolution;
        var alpha = new float[ar, ar, layers.Length];
        for (int z = 0; z < ar; z++)
            for (int x = 0; x < ar; x++)
            {
                Vector2 p = ToWorld(x, z, ar);
                float d = p.magnitude + (Mathf.PerlinNoise(ox + p.x * 0.05f, oz + p.y * 0.05f) - 0.5f) * 14f;
                float forest = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(FieldRadius, ForestStart + 4f, d));
                float dirtNoise = Mathf.PerlinNoise(ox + 300f + p.x * 0.08f, oz + p.y * 0.08f);
                float dirt = Mathf.Clamp01((dirtNoise - 0.62f) * 4f) * (1f - forest);
                float grass = Mathf.Max(0f, 1f - forest - dirt);
                alpha[z, x, 0] = grass;
                alpha[z, x, 1] = dirt;
                alpha[z, x, 2] = forest;
            }
        data.SetAlphamaps(0, 0, alpha);

        // Trees
        var protos = new TreePrototype[trees.Length];
        for (int i = 0; i < trees.Length; i++) protos[i] = new TreePrototype { prefab = trees[i] };
        data.treePrototypes = protos;
        var instances = new List<TreeInstance>();
        int attempts = 0;
        float half = TerrainSize * 0.5f;
        while (instances.Count < 2200 && attempts++ < 40000)
        {
            float x = (float)rng.NextDouble() * TerrainSize - half;
            float z = (float)rng.NextDouble() * TerrainSize - half;
            float d = new Vector2(x, z).magnitude;
            // Denser further in; a few stragglers out into the field edge.
            float density = Mathf.InverseLerp(ForestStart - 8f, ForestStart + 25f, d);
            if (d < FieldRadius + 2f || rng.NextDouble() > density) continue;
            if (Mathf.Abs(x) > half - 2f || Mathf.Abs(z) > half - 2f) continue;
            // Pines dominate the deep forest, broadleaf trees nearer the field.
            bool pine = rng.NextDouble() < Mathf.Lerp(0.35f, 0.8f, Mathf.InverseLerp(ForestStart, half, d));
            int proto = pine ? rng.Next(0, 2) : rng.Next(2, 4);
            float s = 0.8f + (float)rng.NextDouble() * 0.6f;
            instances.Add(new TreeInstance
            {
                prototypeIndex = proto,
                position = new Vector3((x + half) / TerrainSize, 0f, (z + half) / TerrainSize),
                widthScale = s * (0.9f + (float)rng.NextDouble() * 0.2f),
                heightScale = s,
                rotation = (float)(rng.NextDouble() * Mathf.PI * 2),
                color = Color.Lerp(Color.white, new Color(0.85f, 0.9f, 0.8f), (float)rng.NextDouble()),
                lightmapColor = Color.white,
            });
        }
        data.SetTreeInstances(instances.ToArray(), true);

        // Grass
        var detail = new DetailPrototype
        {
            prototypeTexture = grassBlade,
            renderMode = DetailRenderMode.GrassBillboard,
            usePrototypeMesh = false,
            healthyColor = new Color(0.3f, 0.42f, 0.17f),  // matched to the grass ground texture
            dryColor = new Color(0.45f, 0.46f, 0.24f),
            minWidth = 0.4f, maxWidth = 0.7f,
            minHeight = 0.18f, maxHeight = 0.4f,
            noiseSpread = 0.4f,
        };
        data.detailPrototypes = new[] { detail };
        data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
        int dr = data.detailResolution;
        var counts = new int[dr, dr];
        for (int z = 0; z < dr; z++)
            for (int x = 0; x < dr; x++)
            {
                Vector2 p = ToWorld(x, z, dr);
                float d = p.magnitude;
                if (d > ForestStart + 15f) continue;
                float clump = Mathf.PerlinNoise(ox + 500f + p.x * 0.1f, oz + p.y * 0.1f);
                float edgeFade = 1f - Mathf.InverseLerp(FieldRadius, ForestStart + 15f, d);
                counts[z, x] = Mathf.RoundToInt(Mathf.Clamp01(clump * 1.6f - 0.5f) * 3f * edgeFade);
            }
        data.SetDetailLayer(0, 0, 0, counts);
        data.wavingGrassAmount = 0.3f;
        data.wavingGrassSpeed = 0.4f;
        data.wavingGrassStrength = 0.4f;
        data.wavingGrassTint = new Color(0.55f, 0.6f, 0.45f);

        var go = Terrain.CreateTerrainGameObject(data);
        go.name = "ForestTerrain";
        go.transform.position = new Vector3(-half, baseY, -half);
        var terrain = go.GetComponent<Terrain>();
        terrain.drawInstanced = true;
        terrain.materialTemplate = TerrainMaterial(layers);
        terrain.treeDistance = 260f; // fog hides trees well before this
        terrain.treeBillboardDistance = 260f; // always full meshes
        terrain.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On; // single-sided (meshes are closed)
        terrain.detailObjectDistance = 70f;
        terrain.detailObjectDensity = 1f;
        terrain.heightmapPixelError = 5f;
        terrain.basemapDistance = 200f;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
        return terrain;
    }

    static Vector2 ToWorld(int x, int z, int res)
    {
        float half = TerrainSize * 0.5f;
        return new Vector2(x / (float)(res - 1) * TerrainSize - half, z / (float)(res - 1) * TerrainSize - half);
    }

    static float GroundY(Terrain t, float x, float z) => t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y;

    // ---------- Textures & layers ----------
    static TerrainLayer[] BuildTerrainLayers()
    {
        var grass = SaveTexture("Ground_Grass", 512, (u, v) =>
        {
            float n = TileNoise(u, v, 8, 1) * 0.5f + TileNoise(u, v, 32, 2) * 0.35f + TileNoise(u, v, 128, 3) * 0.15f;
            var a = new Color(0.27f, 0.42f, 0.16f); var b = new Color(0.45f, 0.58f, 0.24f);
            var c = Color.Lerp(a, b, n);
            float speck = TileNoise(u, v, 96, 4);
            if (speck > 0.72f) c = Color.Lerp(c, new Color(0.62f, 0.66f, 0.32f), (speck - 0.72f) * 3f);
            return c;
        });
        var dirt = SaveTexture("Ground_Dirt", 512, (u, v) =>
        {
            float n = TileNoise(u, v, 6, 5) * 0.45f + TileNoise(u, v, 24, 6) * 0.35f + TileNoise(u, v, 96, 7) * 0.2f;
            var c = Color.Lerp(new Color(0.33f, 0.26f, 0.18f), new Color(0.5f, 0.41f, 0.29f), n);
            float pebble = TileNoise(u, v, 64, 8);
            if (pebble > 0.75f) c = Color.Lerp(c, new Color(0.55f, 0.53f, 0.5f), (pebble - 0.75f) * 3f);
            return c;
        });
        var floor = SaveTexture("Ground_ForestFloor", 512, (u, v) =>
        {
            float n = TileNoise(u, v, 8, 9) * 0.5f + TileNoise(u, v, 40, 10) * 0.5f;
            var c = Color.Lerp(new Color(0.17f, 0.15f, 0.09f), new Color(0.3f, 0.26f, 0.15f), n);
            float needles = TileNoise(u, v, 160, 11);
            if (needles > 0.6f) c = Color.Lerp(c, new Color(0.45f, 0.3f, 0.15f), (needles - 0.6f) * 2f);
            float moss = TileNoise(u, v, 12, 12);
            if (moss > 0.65f) c = Color.Lerp(c, new Color(0.22f, 0.32f, 0.12f), (moss - 0.65f) * 2.5f);
            return c;
        });
        return new[]
        {
            ImportedGrassLayer() ?? Layer("Grass", grass, 5f),
            Layer("Dirt", dirt, 4f),
            Layer("ForestFloor", floor, 6f),
        };
    }

    static TerrainLayer Layer(string name, Texture2D tex, float tile)
    {
        string path = $"{Dir}/Layer_{name}.terrainlayer";
        var l = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
        if (l == null) { l = new TerrainLayer(); AssetDatabase.CreateAsset(l, path); }
        l.diffuseTexture = tex;
        l.tileSize = new Vector2(tile, tile);
        l.smoothness = 0.05f;
        l.metallic = 0f;
        EditorUtility.SetDirty(l);
        return l;
    }

    // The baked grass ground set in GrassGround/ (color, OpenGL normal, roughness; one tile = 4 m), when present.
    // URP terrain reads smoothness from the mask map's alpha, so roughness is packed into one:
    // R metallic 0, G occlusion 1, B height (from color luminance, for height blending), A 1 - roughness.
    const string GrassDir = Dir + "/GrassGround";
    const float GrassTile = 4f;

    static TerrainLayer ImportedGrassLayer()
    {
        string colorPath = GrassDir + "/grass_color.png", normalPath = GrassDir + "/grass_normal.png", roughPath = GrassDir + "/grass_roughness.png";
        if (!File.Exists(colorPath) || !File.Exists(normalPath) || !File.Exists(roughPath)) return null;

        ConfigureTexture(colorPath, TextureImporterType.Default, true);
        ConfigureTexture(normalPath, TextureImporterType.NormalMap, false);
        ConfigureTexture(roughPath, TextureImporterType.Default, false, readable: true);
        ConfigureTexture(colorPath, TextureImporterType.Default, true, readable: true);

        var color = AssetDatabase.LoadAssetAtPath<Texture2D>(colorPath);
        var rough = AssetDatabase.LoadAssetAtPath<Texture2D>(roughPath);
        int size = rough.width;
        var c = color.GetPixels32();
        var r = rough.GetPixels32();
        var mask = new Color32[r.Length];
        for (int i = 0; i < r.Length; i++)
        {
            byte lum = (byte)((c[i].r * 54 + c[i].g * 183 + c[i].b * 19) >> 8);
            mask[i] = new Color32(0, 255, lum, (byte)(255 - r[i].r));
        }
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
        tex.SetPixels32(mask);
        tex.Apply();
        string maskPath = GrassDir + "/grass_mask.png";
        File.WriteAllBytes(maskPath, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(maskPath);
        ConfigureTexture(maskPath, TextureImporterType.Default, false);
        // Only needed readable to build the mask.
        ConfigureTexture(roughPath, TextureImporterType.Default, false);
        ConfigureTexture(colorPath, TextureImporterType.Default, true);

        var l = Layer("Grass", color, GrassTile);
        l.normalMapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath);
        l.normalScale = 1f;
        l.maskMapTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
        l.smoothness = 1f; // scales the mask map's smoothness
        EditorUtility.SetDirty(l);
        return l;
    }

    // URP terrain only samples layer normal and mask maps when the material's keywords say so.
    public static Material TerrainMaterial(TerrainLayer[] layers)
    {
        string path = Dir + "/M_Terrain.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Terrain/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.EnableKeyword("_TERRAIN_INSTANCED_PERPIXEL_NORMAL");
        SetKeyword(m, "_NORMALMAP", System.Array.Exists(layers, l => l.normalMapTexture != null));
        SetKeyword(m, "_MASKMAP", System.Array.Exists(layers, l => l.maskMapTexture != null));
        EditorUtility.SetDirty(m);
        return m;
    }

    static void SetKeyword(Material m, string keyword, bool on)
    {
        if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
    }

    static void ConfigureTexture(string path, TextureImporterType type, bool sRGB, bool readable = false)
    {
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        if (ti.textureType == type && ti.sRGBTexture == sRGB && ti.isReadable == readable && ti.wrapMode == TextureWrapMode.Repeat) return;
        ti.textureType = type;
        ti.sRGBTexture = sRGB;
        ti.isReadable = readable;
        ti.wrapMode = TextureWrapMode.Repeat;
        ti.anisoLevel = 4;
        ti.SaveAndReimport();
    }

    static Texture2D BuildGrassBladeTexture()
    {
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color[size * size];
        var rng = new System.Random(Seed + 1);
        for (int b = 0; b < 14; b++)
        {
            float x0 = 0.12f + (float)rng.NextDouble() * 0.76f;
            float lean = ((float)rng.NextDouble() - 0.5f) * 0.4f;
            float height = 0.55f + (float)rng.NextDouble() * 0.45f;
            float width = 0.025f + (float)rng.NextDouble() * 0.02f;
            float shade = 0.75f + (float)rng.NextDouble() * 0.25f;
            for (int y = 0; y < size; y++)
            {
                float t = y / (float)size;
                if (t > height) break;
                float k = t / height;
                float cx = x0 + lean * k * k;
                float w = width * (1f - k);
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size;
                    if (Mathf.Abs(u - cx) > w) continue;
                    float g = shade * Mathf.Lerp(0.6f, 1f, k);
                    px[y * size + x] = new Color(g, g, g, 1f);
                }
            }
        }
        tex.SetPixels(px);
        tex.Apply();
        string path = Dir + "/GrassBlade.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.alphaIsTransparency = true;
        ti.wrapMode = TextureWrapMode.Clamp;
        ti.mipmapEnabled = true;
        ti.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static Texture2D SaveTexture(string name, int size, System.Func<float, float, Color> pixel)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGB24, true);
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                px[y * size + x] = pixel(x / (float)size, y / (float)size);
        tex.SetPixels(px);
        tex.Apply();
        string path = $"{Dir}/{name}.png";
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);
        var ti = (TextureImporter)AssetImporter.GetAtPath(path);
        ti.wrapMode = TextureWrapMode.Repeat;
        ti.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    // Tileable value noise in [0,1]: lattice wraps at `cells`, so the texture repeats seamlessly.
    static float TileNoise(float u, float v, int cells, int seed)
    {
        float x = u * cells, y = v * cells;
        int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        float a = Hash(x0, y0, cells, seed), b = Hash(x0 + 1, y0, cells, seed);
        float c = Hash(x0, y0 + 1, cells, seed), d = Hash(x0 + 1, y0 + 1, cells, seed);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    static float Hash(int x, int y, int period, int seed)
    {
        x = ((x % period) + period) % period;
        y = ((y % period) + period) % period;
        unchecked
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 2147483647);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
        }
    }

    // ---------- Procedural meshes ----------
    class MeshBuilder
    {
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector3> normals = new List<Vector3>();
        readonly List<int>[] subs;

        public MeshBuilder(int subMeshes)
        {
            subs = new List<int>[subMeshes];
            for (int i = 0; i < subMeshes; i++) subs[i] = new List<int>();
        }

        // Flat-shaded triangle, wound so it faces away from `inside`.
        public void Tri(int sub, Vector3 a, Vector3 b, Vector3 c, Vector3 inside)
        {
            var n = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(n, (a + b + c) / 3f - inside) < 0f) { (b, c) = (c, b); n = -n; }
            n.Normalize();
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            normals.Add(n); normals.Add(n); normals.Add(n);
            subs[sub].Add(i); subs[sub].Add(i + 1); subs[sub].Add(i + 2);
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name };
            if (verts.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.subMeshCount = subs.Length;
            for (int i = 0; i < subs.Length; i++) m.SetTriangles(subs[i], i);
            m.RecalculateBounds();
            return m;
        }
    }

    static float Jitter(System.Random rng, float amount) => ((float)rng.NextDouble() * 2f - 1f) * amount;

    static void Frustum(MeshBuilder mb, int sub, Vector3 baseC, float r0, float r1, float h, int segs, System.Random rng, float jitter)
    {
        var bottom = new Vector3[segs];
        var top = new Vector3[segs];
        float rot = (float)rng.NextDouble() * Mathf.PI * 2;
        for (int i = 0; i < segs; i++)
        {
            float a = rot + i * Mathf.PI * 2 / segs;
            var dir = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            bottom[i] = baseC + dir * r0 * (1 + Jitter(rng, jitter));
            top[i] = baseC + Vector3.up * h + dir * r1 * (1 + Jitter(rng, jitter));
        }
        var inside = baseC + Vector3.up * h * 0.5f;
        for (int i = 0; i < segs; i++)
        {
            int j = (i + 1) % segs;
            mb.Tri(sub, bottom[i], bottom[j], top[j], inside);
            mb.Tri(sub, bottom[i], top[j], top[i], inside);
        }
    }

    static void Cone(MeshBuilder mb, int sub, Vector3 baseC, float r, float h, int segs, System.Random rng)
    {
        var apex = baseC + Vector3.up * h + new Vector3(Jitter(rng, r * 0.08f), 0, Jitter(rng, r * 0.08f));
        var ring = new Vector3[segs];
        float rot = (float)rng.NextDouble() * Mathf.PI * 2;
        for (int i = 0; i < segs; i++)
        {
            float a = rot + i * Mathf.PI * 2 / segs;
            // Ragged hem: alternate tips droop and stick out.
            float rr = r * (i % 2 == 0 ? 1f : 0.82f) * (1 + Jitter(rng, 0.12f));
            ring[i] = baseC + new Vector3(Mathf.Cos(a) * rr, (i % 2 == 0 ? -0.25f : 0.05f) * r * 0.4f, Mathf.Sin(a) * rr);
        }
        var inside = baseC + Vector3.up * h * 0.3f;
        for (int i = 0; i < segs; i++)
        {
            int j = (i + 1) % segs;
            mb.Tri(sub, ring[i], ring[j], apex, inside);
            mb.Tri(sub, ring[i], ring[j], baseC + Vector3.up * h * 0.15f, apex);
        }
    }

    static int[] IcoTris;
    static readonly Vector3[] IcoVerts = BuildIco(out IcoTris);

    static Vector3[] BuildIco(out int[] tris)
    {
        float t = (1f + Mathf.Sqrt(5f)) / 2f;
        var v = new List<Vector3>
        {
            new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
            new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
            new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
        };
        var f = new List<int>
        {
            0,11,5, 0,5,1, 0,1,7, 0,7,10, 0,10,11, 1,5,9, 5,11,4, 11,10,2, 10,7,6, 7,1,8,
            3,9,4, 3,4,2, 3,2,6, 3,6,8, 3,8,9, 4,9,5, 2,4,11, 6,2,10, 8,6,7, 9,8,1,
        };
        // One subdivision for a rounder blob.
        var mid = new Dictionary<long, int>();
        System.Func<int, int, int> midpoint = (a, b) =>
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            if (mid.TryGetValue(key, out int idx)) return idx;
            v.Add((v[a] + v[b]) * 0.5f);
            mid[key] = v.Count - 1;
            return v.Count - 1;
        };
        var f2 = new List<int>();
        for (int i = 0; i < f.Count; i += 3)
        {
            int a = f[i], b = f[i + 1], c = f[i + 2];
            int ab = midpoint(a, b), bc = midpoint(b, c), ca = midpoint(c, a);
            f2.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
        }
        for (int i = 0; i < v.Count; i++) v[i] = v[i].normalized;
        tris = f2.ToArray();
        return v.ToArray();
    }

    static void Blob(MeshBuilder mb, int sub, Vector3 centre, Vector3 radii, System.Random rng, float roughness)
    {
        var p = new Vector3[IcoVerts.Length];
        for (int i = 0; i < p.Length; i++)
            p[i] = centre + Vector3.Scale(IcoVerts[i] * (1 + Jitter(rng, roughness)), radii);
        for (int i = 0; i < IcoTris.Length; i += 3)
            mb.Tri(sub, p[IcoTris[i]], p[IcoTris[i + 1]], p[IcoTris[i + 2]], centre);
    }

    // ---------- Trees ----------
    static GameObject[] BuildTreePrefabs(System.Random rng)
    {
        var bark = Material("Bark", new Color(0.32f, 0.23f, 0.15f), 0.1f);
        var pineLeaves = Material("PineNeedles", new Color(0.16f, 0.3f, 0.15f), 0.05f);
        var pineLeaves2 = Material("PineNeedlesDark", new Color(0.12f, 0.24f, 0.13f), 0.05f);
        var leaves = Material("Leaves", new Color(0.3f, 0.47f, 0.17f), 0.05f);
        var leaves2 = Material("LeavesYellow", new Color(0.4f, 0.5f, 0.18f), 0.05f);

        var result = new GameObject[4];
        for (int v = 0; v < 2; v++)
        {
            var mb = new MeshBuilder(2);
            float height = v == 0 ? 11f : 14f;
            Frustum(mb, 0, Vector3.down * 0.3f, 0.32f, 0.12f, height * 0.75f, 7, rng, 0.08f);
            int tiers = v == 0 ? 4 : 5;
            for (int i = 0; i < tiers; i++)
            {
                float k = i / (float)tiers;
                float y = height * (0.22f + k * 0.62f);
                float r = Mathf.Lerp(2.6f, 0.9f, k) * (v == 0 ? 1f : 0.9f);
                Cone(mb, 1, Vector3.up * y, r, height * 0.32f, 9, rng);
            }
            result[v] = TreePrefab($"Pine_{v + 1}", mb, bark, v == 0 ? pineLeaves : pineLeaves2, 0.3f, height);
        }
        for (int v = 0; v < 2; v++)
        {
            var mb = new MeshBuilder(2);
            float trunkH = v == 0 ? 3.6f : 4.4f;
            Frustum(mb, 0, Vector3.down * 0.3f, 0.38f, 0.22f, trunkH + 1.2f, 7, rng, 0.1f);
            int blobs = v == 0 ? 5 : 7;
            for (int i = 0; i < blobs; i++)
            {
                float a = i * 2.4f + (float)rng.NextDouble();
                float rad = i == 0 ? 0f : 1.4f + (float)rng.NextDouble() * 0.8f;
                var c = new Vector3(Mathf.Cos(a) * rad, trunkH + 1.6f + Jitter(rng, 0.8f) + (i == 0 ? 1.2f : 0f), Mathf.Sin(a) * rad);
                float s = 1.7f + (float)rng.NextDouble() * 0.9f;
                Blob(mb, 1, c, new Vector3(s, s * 0.85f, s), rng, 0.12f);
            }
            result[2 + v] = TreePrefab($"Broadleaf_{v + 1}", mb, bark, v == 0 ? leaves : leaves2, 0.38f, trunkH + 5f);
        }
        return result;
    }

    static GameObject TreePrefab(string name, MeshBuilder mb, Material bark, Material leaves, float trunkRadius, float height)
    {
        string meshPath = $"{Dir}/Meshes/{name}.asset";
        EnsureFolder(Dir + "/Meshes");
        AssetDatabase.DeleteAsset(meshPath);
        var mesh = mb.ToMesh(name);
        AssetDatabase.CreateAsset(mesh, meshPath);

        var go = new GameObject(name);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        // Terrain warns these aren't "Nature/Soft Occlusion" trees; that only matters for legacy billboards,
        // which are pushed out of range in BuildTerrain, so the warning is harmless.
        go.AddComponent<MeshRenderer>().sharedMaterials = new[] { bark, leaves };
        // Trunk collider only, so you can push through foliage but not trunks.
        var col = go.AddComponent<CapsuleCollider>();
        col.radius = trunkRadius;
        col.height = height;
        col.center = new Vector3(0, height / 2f, 0);
        EnsureFolder(Dir + "/Prefabs");
        var prefab = PrefabUtility.SaveAsPrefabAsset(go, $"{Dir}/Prefabs/{name}.prefab");
        Object.DestroyImmediate(go);
        return prefab;
    }

    static Material Material(string name, Color color, float smoothness)
    {
        string path = $"{Dir}/M_{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", smoothness);
        m.SetFloat("_Metallic", 0f);
        m.enableInstancing = true; // terrain draws trees instanced
        EditorUtility.SetDirty(m);
        return m;
    }

    // ---------- Rocks ----------
    static Mesh[] BuildRockMeshes(System.Random rng)
    {
        EnsureFolder(Dir + "/Meshes");
        var meshes = new Mesh[3];
        for (int i = 0; i < meshes.Length; i++)
        {
            var mb = new MeshBuilder(1);
            Blob(mb, 0, Vector3.zero, new Vector3(1f, 0.6f + i * 0.15f, 0.8f + i * 0.1f), rng, 0.22f);
            string path = $"{Dir}/Meshes/Rock_{i + 1}.asset";
            AssetDatabase.DeleteAsset(path);
            meshes[i] = mb.ToMesh("Rock_" + (i + 1));
            AssetDatabase.CreateAsset(meshes[i], path);
        }
        return meshes;
    }

    static void PlaceRocks(Terrain terrain, Mesh[] meshes, System.Random rng)
    {
        var mat = Material("Rock", new Color(0.45f, 0.44f, 0.42f), 0.15f);
        var parent = new GameObject("Rocks").transform;
        int placed = 0, attempts = 0;
        while (placed < 70 && attempts++ < 2000)
        {
            float x = Jitter(rng, TerrainSize * 0.45f), z = Jitter(rng, TerrainSize * 0.45f);
            float d = new Vector2(x, z).magnitude;
            // A few boulders out in the field for cover, the rest in the forest. Keep the spawn area clear.
            bool field = d > 18f && d < FieldRadius && placed < 10;
            if (!field && d < ForestStart) continue;
            var go = new GameObject("Rock");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = meshes[rng.Next(meshes.Length)];
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            go.AddComponent<MeshCollider>().convex = true;
            float s = field ? 0.8f + (float)rng.NextDouble() * 0.9f : 0.5f + (float)rng.NextDouble() * 1.8f;
            go.transform.localScale = new Vector3(s, s, s) * (1 + Jitter(rng, 0.15f));
            go.transform.position = new Vector3(x, GroundY(terrain, x, z) + s * 0.15f, z);
            go.transform.rotation = Quaternion.Euler(Jitter(rng, 8f), (float)rng.NextDouble() * 360f, Jitter(rng, 8f));
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
            placed++;
        }
    }

    // ---------- Boundary ----------
    static void BuildBoundary()
    {
        var parent = new GameObject("Boundary").transform;
        float e = TerrainSize * 0.5f - BoundaryInset;
        foreach (var (pos, size) in new[]
        {
            (new Vector3(0, 30, e), new Vector3(e * 2, 80, 1)), (new Vector3(0, 30, -e), new Vector3(e * 2, 80, 1)),
            (new Vector3(e, 30, 0), new Vector3(1, 80, e * 2)), (new Vector3(-e, 30, 0), new Vector3(1, 80, e * 2)),
        })
        {
            var w = new GameObject("Wall");
            w.transform.SetParent(parent, false);
            w.transform.position = pos;
            w.AddComponent<BoxCollider>().size = size;
        }
    }

    // ---------- Targets, crates, player ----------
    static void PlaceGameplayObjects(Terrain terrain)
    {
        var spawn = new Vector3(0, 0, -9);
        var targets = GameObject.Find("Targets");
        if (targets != null)
        {
            // Spread out across the field at a range of distances.
            Vector3[] spots = { new Vector3(-6, 0, 8), new Vector3(7, 0, 15), new Vector3(-14, 0, 24), new Vector3(16, 0, 30), new Vector3(1, 0, 38) };
            int i = 0;
            foreach (Transform t in targets.transform)
            {
                if (i >= spots.Length) break;
                var p = spots[i++];
                p.y = GroundY(terrain, p.x, p.z);
                t.position = p;
                var look = spawn - p; look.y = 0;
                t.rotation = Quaternion.LookRotation(-look.normalized);
            }
        }
        var crates = GameObject.Find("PhysicsCrates");
        if (crates != null)
            foreach (Transform c in crates.transform)
            {
                var p = c.position;
                p.y = GroundY(terrain, p.x, p.z) + 0.3f;
                c.position = p;
            }
        var player = GameObject.Find("Player");
        if (player != null) player.transform.position = new Vector3(spawn.x, GroundY(terrain, spawn.x, spawn.z) + 0.05f, spawn.z);
    }

    // ---------- Lighting & fog ----------
    static void SetupAtmosphere()
    {
        var sun = Object.FindAnyObjectByType<Light>();
        foreach (var l in Object.FindObjectsByType<Light>())
            if (l.type == LightType.Directional) sun = l;
        if (sun != null && sun.type == LightType.Directional)
        {
            sun.transform.rotation = Quaternion.Euler(42f, -35f, 0f);
            sun.color = new Color(1f, 0.95f, 0.85f);
            sun.intensity = 1.3f;
            sun.shadows = LightShadows.Soft;
        }
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.ExponentialSquared;
        RenderSettings.fogDensity = 0.007f;
        RenderSettings.fogColor = new Color(0.68f, 0.74f, 0.78f);
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
