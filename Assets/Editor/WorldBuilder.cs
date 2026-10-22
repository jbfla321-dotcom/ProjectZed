using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Builds the open world around the forest field: a 2 km terrain with the existing field terrain (ForestBuilder) left
// exactly where it is in the middle, roads leading out of it, and a handful of locations to find.
// Everything here is deliberately simple placeholder content: edit the Locations / Roads lists below and re-run
// Tools > Environment > Build World. Run Build Forest Field first if you ever rebuild that.
//
// - The world terrain has a hole where the field terrain sits; their edges are made to match exactly.
// - Roads cut a level bed through both terrains (removing trees, rocks and grass in the way) and get a simple mesh.
// - The field terrain's original data is kept in a backup asset, so re-running always starts from it.
public static class WorldBuilder
{
    const string Dir = "Assets/Environment/World";
    const int Seed = 4321;

    // 2048 m (not 2000) so the 2 m terrain grid lines up exactly with the field terrain's edges (+-160 m).
    const float WorldSize = 2048f;
    const int HeightRes = 1025;
    const float BaseY = -2f;           // same as the field terrain: y = 0 is the field
    const float WorldHeight = 90f;
    const float TileHalf = 160f;       // the field terrain spans -160..160
    const float SeamBlend = 140f;      // the outer landscape eases in over this distance from the field terrain
    const float BoundaryInset = 8f;
    const float Shoulder = 8f;         // terrain blends back to natural over this distance beside a road

    // ---------- Content (edit these) ----------
    class Location
    {
        public string name;
        public Vector2 position;
        public float radius;     // flattened clearing
        public int buildings;    // placeholder boxes
    }

    class Road
    {
        public string name;
        public float width;
        public bool paved;
        public Vector2[] points; // control points (x, z); the road is a smooth curve through them
    }

    static readonly Location[] Locations =
    {
        new Location { name = "North Village", position = new Vector2(-60f, 720f), radius = 55f, buildings = 7 },
        new Location { name = "NE Outpost", position = new Vector2(650f, 650f), radius = 35f, buildings = 3 },
        new Location { name = "East Farm", position = new Vector2(720f, 60f), radius = 50f, buildings = 4 },
        new Location { name = "Southfield", position = new Vector2(40f, -700f), radius = 55f, buildings = 6 },
        new Location { name = "SW Camp", position = new Vector2(-650f, -650f), radius = 35f, buildings = 3 },
        new Location { name = "West Quarry", position = new Vector2(-740f, -40f), radius = 50f, buildings = 3 },
    };

    // The paved roads meet just south of the parked cars (z = -25); the north track leaves past the targets.
    static readonly Road[] Roads =
    {
        new Road { name = "Main Road", width = 7f, paved = true, points = new[]
        {
            new Vector2(-740f, -40f), new Vector2(-560f, -70f), new Vector2(-350f, -20f), new Vector2(-170f, -45f),
            new Vector2(-60f, -36f), new Vector2(0f, -36f), new Vector2(60f, -36f), new Vector2(170f, -25f),
            new Vector2(380f, 40f), new Vector2(560f, 15f), new Vector2(720f, 60f),
        } },
        new Road { name = "South Road", width = 7f, paved = true, points = new[]
        {
            new Vector2(0f, -36f), new Vector2(0f, -90f), new Vector2(20f, -200f), new Vector2(-25f, -400f),
            new Vector2(45f, -560f), new Vector2(40f, -700f),
        } },
        new Road { name = "North Track", width = 4.5f, paved = false, points = new[]
        {
            new Vector2(35f, 45f), new Vector2(60f, 95f), new Vector2(40f, 170f), new Vector2(-30f, 350f),
            new Vector2(-40f, 560f), new Vector2(-60f, 720f),
        } },
        new Road { name = "NE Track", width = 4.5f, paved = false, points = new[]
        {
            new Vector2(-60f, 720f), new Vector2(150f, 770f), new Vector2(420f, 700f), new Vector2(650f, 650f),
            new Vector2(730f, 400f), new Vector2(720f, 60f),
        } },
        new Road { name = "SW Track", width = 4.5f, paved = false, points = new[]
        {
            new Vector2(40f, -700f), new Vector2(-250f, -730f), new Vector2(-650f, -650f), new Vector2(-770f, -350f),
            new Vector2(-740f, -40f),
        } },
    };

    // ---------- Build ----------
    [MenuItem("Tools/Environment/Build World")]
    public static void Build()
    {
        var tileGo = GameObject.Find("ForestTerrain");
        if (tileGo == null) { Debug.LogError("[WorldBuilder] No ForestTerrain: run Tools > Environment > Build Forest Field first."); return; }
        EnsureFolder(Dir);
        var tile = tileGo.GetComponent<Terrain>();
        var original = BackupOf(tile.terrainData);
        RestoreTile(tile.terrainData, original);

        var old = GameObject.Find("World");
        if (old != null) Object.DestroyImmediate(old);
        var root = new GameObject("World").transform;

        var rng = new System.Random(Seed);
        noiseX = (float)rng.NextDouble() * 1000f;
        noiseZ = (float)rng.NextDouble() * 1000f;
        tileData = original;

        var roads = new List<RoadPath>();
        foreach (var def in Roads) roads.Add(BuildPath(def));

        // Field terrain first (roads cut through its hills), then the world around it, matched to its edge.
        var tileGrid = new Grid(new Vector2(-TileHalf, -TileHalf), tile.terrainData.size.x / (tile.terrainData.heightmapResolution - 1), tile.terrainData.heightmapResolution);
        tileGrid.Stamp(roads);
        CarveTile(tile, tileGrid);

        var world = BuildWorldTerrain(tile, roads, rng);
        world.transform.SetParent(root, true);
        MatchTileEdge(tile, world);
        AddSeamSkirt(tile, world);

        BuildRoadMeshes(roads, root);
        BuildLocations(world, roads, root, rng);
        BuildBoundary(root);
        HideFieldObstacles(roads);

        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[WorldBuilder] Built {WorldSize}m world: {Roads.Length} roads, {Locations.Length} locations.");
    }

    // ---------- Heights ----------
    static float noiseX, noiseZ;
    static TerrainData tileData; // the field terrain as originally built

    static float TileHeight(TerrainData data, float x, float z) =>
        BaseY + data.GetInterpolatedHeight(Mathf.Clamp01((x + TileHalf) / (2f * TileHalf)), Mathf.Clamp01((z + TileHalf) / (2f * TileHalf)));

    // The open landscape: broad hills, smaller rolls and a little roughness (world y).
    static float Landscape(float x, float z)
    {
        float hills = Mathf.PerlinNoise(noiseX + x * 0.0018f, noiseZ + z * 0.0018f);
        float rolls = Mathf.PerlinNoise(noiseX + 77f + x * 0.007f, noiseZ + 31f + z * 0.007f) - 0.5f;
        float rough = Mathf.PerlinNoise(noiseX + 191f + x * 0.03f, noiseZ + 53f + z * 0.03f) - 0.5f;
        return 2f + hills * hills * 36f + rolls * 9f + rough * 1.2f + EdgeRidge(x, z) * 30f;
    }

    // 0..1: rises into a ring of hills over the last stretch before the map edge, so the horizon is hills rather than
    // the end of the terrain.
    const float EdgeRidgeWidth = 180f;
    static float EdgeRidge(float x, float z)
    {
        float toEdge = WorldSize * 0.5f - Mathf.Max(Mathf.Abs(x), Mathf.Abs(z))
                       + (Mathf.PerlinNoise(noiseX + 222f + x * 0.01f, noiseZ + 333f + z * 0.01f) - 0.5f) * 60f;
        return 1f - Mathf.SmoothStep(0f, 1f, toEdge / EdgeRidgeWidth);
    }

    // Natural ground height before roads (world y). Inside the field terrain: its own height.
    // Outside: its edge eases into the landscape; locations are levelled.
    static float Natural(float x, float z, TerrainData edge)
    {
        float outside = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) - TileHalf;
        if (outside <= 0f) return TileHeight(tileData, x, z);
        float seam = TileHeight(edge, Mathf.Clamp(x, -TileHalf, TileHalf), Mathf.Clamp(z, -TileHalf, TileHalf));
        float h = Mathf.Lerp(seam, Landscape(x, z), Mathf.SmoothStep(0f, 1f, outside / SeamBlend));
        foreach (var loc in Locations)
        {
            float d = Vector2.Distance(new Vector2(x, z), loc.position);
            if (d < loc.radius + 30f)
                h = Mathf.Lerp(LocationHeight(loc), h, Mathf.SmoothStep(0f, 1f, (d - loc.radius) / 30f));
        }
        return Mathf.Clamp(h, BaseY + 0.5f, BaseY + WorldHeight - 1f);
    }

    static float LocationHeight(Location loc) => Landscape(loc.position.x, loc.position.y);

    // ---------- Roads ----------
    class RoadPath
    {
        public Road def;
        public List<Vector2> points = new List<Vector2>();
        public List<float> heights = new List<float>();
        public float HalfWidth => def.width * 0.5f;
    }

    const float SampleSpacing = 2f;

    static RoadPath BuildPath(Road def)
    {
        var path = new RoadPath { def = def };
        var p = def.points;
        for (int i = 0; i < p.Length - 1; i++)
        {
            Vector2 p0 = p[Mathf.Max(i - 1, 0)], p1 = p[i], p2 = p[i + 1], p3 = p[Mathf.Min(i + 2, p.Length - 1)];
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(p1, p2) / SampleSpacing));
            for (int s = 0; s < steps; s++) path.points.Add(CatmullRom(p0, p1, p2, p3, s / (float)steps));
        }
        path.points.Add(p[p.Length - 1]);

        // Road level: the natural ground averaged over +-60 m, then held to a drivable grade, so it climbs gently and
        // cuts through hills rather than going over them. In the flat field it stays exactly on the ground.
        int count = path.points.Count;
        var natural = new float[count];
        for (int i = 0; i < count; i++) natural[i] = Natural(path.points[i].x, path.points[i].y, tileData);
        var level = new float[count];
        const int window = 30;
        for (int i = 0; i < count; i++)
        {
            float sum = 0f; int n = 0;
            for (int j = Mathf.Max(0, i - window); j <= Mathf.Min(count - 1, i + window); j++) { sum += natural[j]; n++; }
            float field = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(50f, 80f, path.points[i].magnitude));
            level[i] = Mathf.Lerp(natural[i], sum / n, field);
        }
        float maxGrade = def.paved ? MaxGradePaved : MaxGradeTrack;
        for (int pass = 0; pass < 4; pass++)
        {
            for (int i = 1; i < count; i++) level[i] = LimitGrade(level[i], level[i - 1], path.points[i], path.points[i - 1], maxGrade);
            for (int i = count - 2; i >= 0; i--) level[i] = LimitGrade(level[i], level[i + 1], path.points[i], path.points[i + 1], maxGrade);
        }
        path.heights.AddRange(level);
        return path;
    }

    const float MaxGradePaved = 0.1f, MaxGradeTrack = 0.14f;

    static float LimitGrade(float h, float neighbour, Vector2 p, Vector2 q, float grade)
    {
        float rise = grade * Vector2.Distance(p, q);
        return Mathf.Clamp(h, neighbour - rise, neighbour + rise);
    }

    static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3);
    }

    // For each vertex of a terrain grid: distance to the nearest road, and that road's level and half width there.
    class Grid
    {
        public readonly Vector2 origin;
        public readonly float spacing;
        public readonly int res;
        public readonly float[] dist, level, halfWidth;

        public Grid(Vector2 origin, float spacing, int res)
        {
            this.origin = origin; this.spacing = spacing; this.res = res;
            dist = new float[res * res];
            level = new float[res * res];
            halfWidth = new float[res * res];
            for (int i = 0; i < dist.Length; i++) dist[i] = float.MaxValue;
        }

        public Vector2 Point(int x, int z) => origin + new Vector2(x, z) * spacing;

        public void Stamp(List<RoadPath> roads)
        {
            foreach (var road in roads)
            {
                float reach = road.HalfWidth + Shoulder + 6f;
                for (int i = 0; i < road.points.Count - 1; i++)
                {
                    Vector2 a = road.points[i], b = road.points[i + 1];
                    int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - reach - origin.x) / spacing));
                    int x1 = Mathf.Min(res - 1, Mathf.CeilToInt((Mathf.Max(a.x, b.x) + reach - origin.x) / spacing));
                    int z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.y, b.y) - reach - origin.y) / spacing));
                    int z1 = Mathf.Min(res - 1, Mathf.CeilToInt((Mathf.Max(a.y, b.y) + reach - origin.y) / spacing));
                    Vector2 ab = b - a;
                    float len2 = Mathf.Max(ab.sqrMagnitude, 1e-6f);
                    for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++)
                        {
                            Vector2 p = Point(x, z);
                            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                            float d = Vector2.Distance(p, a + ab * t);
                            int k = z * res + x;
                            if (d >= dist[k]) continue;
                            dist[k] = d;
                            level[k] = Mathf.Lerp(road.heights[i], road.heights[i + 1], t);
                            halfWidth[k] = road.HalfWidth;
                        }
                }
            }
        }

        // How much the ground at vertex k is set to the road level (1 under the road, easing to 0 beside it).
        public float CarveWeight(int k)
        {
            float edge = halfWidth[k] + 0.75f;
            return dist[k] <= edge ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (dist[k] - edge) / Shoulder);
        }

        // Nearest-vertex lookup for a world point (for trees, grass, paint at other resolutions).
        public int Nearest(float wx, float wz)
        {
            int x = Mathf.Clamp(Mathf.RoundToInt((wx - origin.x) / spacing), 0, res - 1);
            int z = Mathf.Clamp(Mathf.RoundToInt((wz - origin.y) / spacing), 0, res - 1);
            return z * res + x;
        }
    }

    // ---------- Field terrain ----------
    static TerrainData BackupOf(TerrainData live)
    {
        string path = Dir + "/ForestTerrain_Original.asset";
        var backup = AssetDatabase.LoadAssetAtPath<TerrainData>(path);
        if (backup != null) return backup;
        AssetDatabase.CopyAsset(AssetDatabase.GetAssetPath(live), path);
        return AssetDatabase.LoadAssetAtPath<TerrainData>(path);
    }

    static void RestoreTile(TerrainData live, TerrainData original)
    {
        int hr = original.heightmapResolution, ar = original.alphamapResolution, dr = original.detailResolution;
        // Build PNW Forest swaps these on the live terrain; put the field's own back first so the data below fits.
        live.terrainLayers = original.terrainLayers;
        live.treePrototypes = original.treePrototypes;
        live.detailPrototypes = original.detailPrototypes;
        live.SetHeights(0, 0, original.GetHeights(0, 0, hr, hr));
        live.SetAlphamaps(0, 0, original.GetAlphamaps(0, 0, ar, ar));
        live.SetTreeInstances(original.treeInstances, false);
        for (int l = 0; l < original.detailPrototypes.Length; l++)
            live.SetDetailLayer(0, 0, l, original.GetDetailLayer(0, 0, dr, dr, l));
    }

    static void CarveTile(Terrain tile, Grid grid)
    {
        var data = tile.terrainData;
        int hr = data.heightmapResolution;
        var heights = data.GetHeights(0, 0, hr, hr);
        for (int z = 0; z < hr; z++)
            for (int x = 0; x < hr; x++)
            {
                int k = z * hr + x;
                if (grid.dist[k] == float.MaxValue) continue;
                float w = grid.CarveWeight(k);
                if (w <= 0f) continue;
                float h = BaseY + heights[z, x] * data.size.y;
                heights[z, x] = Mathf.Clamp01((Mathf.Lerp(h, grid.level[k], w) - BaseY) / data.size.y);
            }
        data.SetHeights(0, 0, heights);

        // Trees and grass off the road; dirt on the shoulders.
        var trees = new List<TreeInstance>();
        foreach (var t in data.treeInstances)
        {
            float wx = t.position.x * data.size.x - TileHalf, wz = t.position.z * data.size.z - TileHalf;
            int k = grid.Nearest(wx, wz);
            if (grid.dist[k] > grid.halfWidth[k] + 4f) trees.Add(t);
        }
        data.SetTreeInstances(trees.ToArray(), true);
        ClearDetails(data, (wx, wz) => { int k = grid.Nearest(wx, wz); return grid.dist[k] < grid.halfWidth[k] + 1.5f; }, -TileHalf);
        PaintShoulders(data, grid, -TileHalf);
    }

    // The field terrain's edge vertices are set to the world terrain's surface there, so there are no cracks.
    static void MatchTileEdge(Terrain tile, Terrain world)
    {
        var data = tile.terrainData;
        int hr = data.heightmapResolution;
        var heights = data.GetHeights(0, 0, hr, hr);
        float step = data.size.x / (hr - 1);
        for (int i = 0; i < hr; i++)
        {
            foreach (var (x, z) in new[] { (i, 0), (i, hr - 1), (0, i), (hr - 1, i) })
            {
                float wx = -TileHalf + x * step, wz = -TileHalf + z * step;
                float h = world.SampleHeight(new Vector3(wx, 0f, wz)) + world.transform.position.y;
                heights[z, x] = Mathf.Clamp01((h - BaseY) / data.size.y);
            }
        }
        data.SetHeights(0, 0, heights);
    }

    // Each terrain simplifies its own mesh with distance, so even with matching edge heights their seam can open
    // hairline cracks onto the sky. The world terrain therefore carries on one hole cell (2 m) under the field
    // terrain's edge, with everything inside its edge SkirtDepth below the field terrain (all of it, not just that
    // ring, so the world's own simplified mesh stays underneath too) and painted like the field terrain there:
    // any crack shows matching ground instead of nothing.
    const float SkirtDepth = 0.4f;

    [MenuItem("Tools/Environment/Fix World Seam")]
    public static void FixWorldSeam()
    {
        var world = GameObject.Find("World/WorldTerrain")?.GetComponent<Terrain>();
        var tile = GameObject.Find("ForestTerrain")?.GetComponent<Terrain>();
        if (world == null || tile == null) { Debug.LogError("[WorldBuilder] WorldTerrain or ForestTerrain not found."); return; }
        AddSeamSkirt(tile, world);
        AssetDatabase.SaveAssets();
        Debug.Log("[WorldBuilder] Seam skirt added under the field terrain's edge.");
    }

    static void AddSeamSkirt(Terrain tile, Terrain world)
    {
        var data = world.terrainData;
        float half = WorldSize * 0.5f;
        int holeRes = data.holesResolution;
        float cell = WorldSize / holeRes;
        float inner = TileHalf - cell;
        var holes = new bool[holeRes, holeRes];
        for (int z = 0; z < holeRes; z++)
            for (int x = 0; x < holeRes; x++)
            {
                float cx = -half + (x + 0.5f) * cell, cz = -half + (z + 0.5f) * cell;
                holes[z, x] = Mathf.Abs(cx) > inner || Mathf.Abs(cz) > inner; // true = solid ground
            }
        data.SetHoles(0, 0, holes);

        // World vertices inside the field's edge sit just below the field terrain.
        int hr = data.heightmapResolution;
        float step = WorldSize / (hr - 1);
        int lo = Mathf.RoundToInt((half - inner) / step), hi = Mathf.RoundToInt((half + inner) / step);
        int n = hi - lo + 1;
        var heights = new float[n, n];
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                var p = new Vector3(-half + (lo + x) * step, 0f, -half + (lo + z) * step);
                float h = tile.SampleHeight(p) + tile.transform.position.y - SkirtDepth;
                heights[z, x] = Mathf.Clamp01((h - BaseY) / WorldHeight);
            }
        data.SetHeights(lo, lo, heights);

        // Paint the strip under the edge with the field terrain's own layer weights (both share the same layers).
        var tileData = tile.terrainData;
        int ar = data.alphamapResolution, tr = tileData.alphamapResolution, layers = data.alphamapLayers;
        float texel = WorldSize / ar;
        int a0 = Mathf.FloorToInt((half - TileHalf) / texel), a1 = Mathf.CeilToInt((half + TileHalf) / texel) - 1;
        int an = a1 - a0 + 1;
        var paint = data.GetAlphamaps(a0, a0, an, an);
        var src = tileData.GetAlphamaps(0, 0, tr, tr);
        for (int z = 0; z < an; z++)
            for (int x = 0; x < an; x++)
            {
                float cx = -half + (a0 + x + 0.5f) * texel, cz = -half + (a0 + z + 0.5f) * texel;
                if (Mathf.Max(Mathf.Abs(cx), Mathf.Abs(cz)) < inner || Mathf.Max(Mathf.Abs(cx), Mathf.Abs(cz)) > TileHalf) continue;
                int sx = Mathf.Clamp((int)((cx + TileHalf) / (2f * TileHalf) * tr), 0, tr - 1);
                int sz = Mathf.Clamp((int)((cz + TileHalf) / (2f * TileHalf) * tr), 0, tr - 1);
                for (int l = 0; l < layers; l++) paint[z, x, l] = l < src.GetLength(2) ? src[sz, sx, l] : 0f;
            }
        data.SetAlphamaps(a0, a0, paint);
    }

    // ---------- World terrain ----------
    static Terrain BuildWorldTerrain(Terrain tile, List<RoadPath> roads, System.Random rng)
    {
        string dataPath = Dir + "/WorldTerrain.asset";
        AssetDatabase.DeleteAsset(dataPath);
        var source = tile.terrainData;
        var data = new TerrainData { heightmapResolution = HeightRes, alphamapResolution = 1024 };
        data.size = new Vector3(WorldSize, WorldHeight, WorldSize);
        data.SetDetailResolution(1024, 32);
        AssetDatabase.CreateAsset(data, dataPath);

        float half = WorldSize * 0.5f;
        var grid = new Grid(new Vector2(-half, -half), WorldSize / (HeightRes - 1), HeightRes);
        grid.Stamp(roads);

        var heights = new float[HeightRes, HeightRes];
        for (int z = 0; z < HeightRes; z++)
            for (int x = 0; x < HeightRes; x++)
            {
                Vector2 p = grid.Point(x, z);
                float h = Natural(p.x, p.y, source); // seam follows the (road-cut) field terrain edge
                int k = z * HeightRes + x;
                if (grid.dist[k] != float.MaxValue) h = Mathf.Lerp(h, grid.level[k], grid.CarveWeight(k));
                heights[z, x] = Mathf.Clamp01((h - BaseY) / WorldHeight);
            }
        data.SetHeights(0, 0, heights);

        // The field terrain shows through a hole of exactly its size (AddSeamSkirt later shrinks it by a cell).
        int holeRes = data.holesResolution;
        var holes = new bool[holeRes, holeRes];
        float cell = WorldSize / holeRes;
        for (int z = 0; z < holeRes; z++)
            for (int x = 0; x < holeRes; x++)
            {
                float cx = -half + (x + 0.5f) * cell, cz = -half + (z + 0.5f) * cell;
                holes[z, x] = Mathf.Abs(cx) > TileHalf || Mathf.Abs(cz) > TileHalf; // true = solid ground
            }
        data.SetHoles(0, 0, holes);

        data.terrainLayers = source.terrainLayers;
        PaintWorld(data, grid);
        data.treePrototypes = source.treePrototypes;
        PlaceWorldTrees(data, grid, rng);
        data.detailPrototypes = source.detailPrototypes;
        data.SetDetailScatterMode(DetailScatterMode.InstanceCountMode);
        PlaceWorldGrass(data, grid);
        data.wavingGrassAmount = source.wavingGrassAmount;
        data.wavingGrassSpeed = source.wavingGrassSpeed;
        data.wavingGrassStrength = source.wavingGrassStrength;
        data.wavingGrassTint = source.wavingGrassTint;

        var go = Terrain.CreateTerrainGameObject(data);
        go.name = "WorldTerrain";
        go.transform.position = new Vector3(-half, BaseY, -half);
        var terrain = go.GetComponent<Terrain>();
        terrain.drawInstanced = tile.drawInstanced;
        terrain.materialTemplate = tile.materialTemplate;
        terrain.treeDistance = tile.treeDistance;
        terrain.treeBillboardDistance = tile.treeBillboardDistance;
        terrain.shadowCastingMode = tile.shadowCastingMode;
        terrain.detailObjectDistance = tile.detailObjectDistance;
        terrain.detailObjectDensity = tile.detailObjectDensity;
        terrain.heightmapPixelError = tile.heightmapPixelError;
        terrain.basemapDistance = tile.basemapDistance;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
        return terrain;
    }

    // 0..1: how much a world point is forest (patches of woodland across the open landscape). The field terrain is
    // forested right to its edge, so that forest carries on outward and thins out, rather than stopping in a square.
    static float ForestMask(float x, float z)
    {
        float n = Mathf.PerlinNoise(noiseX + 400f + x * 0.004f, noiseZ + 250f + z * 0.004f)
                  + (Mathf.PerlinNoise(noiseX + 900f + x * 0.02f, noiseZ + 120f + z * 0.02f) - 0.5f) * 0.25f;
        float patches = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.5f, 0.6f, n));
        float outside = Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) - TileHalf
                        + (Mathf.PerlinNoise(noiseX + 640f + x * 0.03f, noiseZ + 710f + z * 0.03f) - 0.5f) * 50f;
        float fieldForest = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(30f, 120f, outside));
        return Mathf.Max(Mathf.Max(patches, fieldForest), Mathf.Clamp01(EdgeRidge(x, z) * 1.5f));
    }

    static bool InTile(float x, float z, float margin) => Mathf.Abs(x) < TileHalf + margin && Mathf.Abs(z) < TileHalf + margin;

    static float LocationDistance(float x, float z, out Location nearest)
    {
        nearest = null;
        float best = float.MaxValue;
        foreach (var loc in Locations)
        {
            float d = Vector2.Distance(new Vector2(x, z), loc.position) - loc.radius;
            if (d < best) { best = d; nearest = loc; }
        }
        return best;
    }

    // Layers (from the field terrain): 0 grass, 1 dirt, 2 forest floor.
    static void PaintWorld(TerrainData data, Grid grid)
    {
        int ar = data.alphamapResolution;
        float half = WorldSize * 0.5f, cell = WorldSize / ar;
        var alpha = new float[ar, ar, 3];
        for (int z = 0; z < ar; z++)
            for (int x = 0; x < ar; x++)
            {
                float wx = -half + (x + 0.5f) * cell, wz = -half + (z + 0.5f) * cell;
                float forest = ForestMask(wx, wz);
                int k = grid.Nearest(wx, wz);
                float road = grid.dist[k] == float.MaxValue ? 0f : 1f - Mathf.SmoothStep(0f, 1f, (grid.dist[k] - grid.halfWidth[k] - 1f) / 3f);
                float yard = 1f - Mathf.SmoothStep(0f, 1f, LocationDistance(wx, wz, out _) / 12f + 0.3f);
                float dirt = Mathf.Max(road, yard * 0.8f);
                forest *= 1f - dirt;
                alpha[z, x, 0] = Mathf.Max(0f, 1f - forest - dirt);
                alpha[z, x, 1] = dirt;
                alpha[z, x, 2] = forest;
            }
        data.SetAlphamaps(0, 0, alpha);
    }

    static void PlaceWorldTrees(TerrainData data, Grid grid, System.Random rng)
    {
        float half = WorldSize * 0.5f;
        const float spacing = 8f;
        var instances = new List<TreeInstance>();
        for (float z = -half + spacing; z < half - spacing; z += spacing)
            for (float x = -half + spacing; x < half - spacing; x += spacing)
            {
                float wx = x + ((float)rng.NextDouble() - 0.5f) * spacing, wz = z + ((float)rng.NextDouble() - 0.5f) * spacing;
                float chance = Mathf.Lerp(0.015f, 0.85f, ForestMask(wx, wz)); // the odd lone tree in the open
                if (rng.NextDouble() > chance) continue;
                if (InTile(wx, wz, 3f) || Mathf.Abs(wx) > half - BoundaryInset - 4f || Mathf.Abs(wz) > half - BoundaryInset - 4f) continue;
                int k = grid.Nearest(wx, wz);
                if (grid.dist[k] < grid.halfWidth[k] + 5f || LocationDistance(wx, wz, out _) < 6f) continue;
                bool pine = Mathf.PerlinNoise(noiseX + 33f + wx * 0.01f, noiseZ + wz * 0.01f) > 0.45f;
                float s = 0.8f + (float)rng.NextDouble() * 0.6f;
                instances.Add(new TreeInstance
                {
                    prototypeIndex = pine ? rng.Next(0, 2) : rng.Next(2, 4),
                    position = new Vector3((wx + half) / WorldSize, 0f, (wz + half) / WorldSize),
                    widthScale = s * (0.9f + (float)rng.NextDouble() * 0.2f),
                    heightScale = s,
                    rotation = (float)(rng.NextDouble() * Mathf.PI * 2),
                    color = Color.Lerp(Color.white, new Color(0.85f, 0.9f, 0.8f), (float)rng.NextDouble()),
                    lightmapColor = Color.white,
                });
            }
        data.SetTreeInstances(instances.ToArray(), true);
    }

    static void PlaceWorldGrass(TerrainData data, Grid grid)
    {
        int dr = data.detailResolution;
        float half = WorldSize * 0.5f, cell = WorldSize / dr;
        var counts = new int[dr, dr];
        for (int z = 0; z < dr; z++)
            for (int x = 0; x < dr; x++)
            {
                float wx = -half + (x + 0.5f) * cell, wz = -half + (z + 0.5f) * cell;
                if (InTile(wx, wz, 0f)) continue;
                int k = grid.Nearest(wx, wz);
                if (grid.dist[k] < grid.halfWidth[k] + 2f || LocationDistance(wx, wz, out _) < 4f) continue;
                float open = 1f - ForestMask(wx, wz);
                float clump = Mathf.PerlinNoise(noiseX + 500f + wx * 0.1f, noiseZ + wz * 0.1f);
                counts[z, x] = Mathf.RoundToInt(Mathf.Clamp01(clump * 1.6f - 0.5f) * 10f * open);
            }
        data.SetDetailLayer(0, 0, 0, counts);
    }

    static void ClearDetails(TerrainData data, System.Func<float, float, bool> clear, float originXZ)
    {
        int dr = data.detailResolution;
        float cell = data.size.x / dr;
        for (int l = 0; l < data.detailPrototypes.Length; l++)
        {
            var counts = data.GetDetailLayer(0, 0, dr, dr, l);
            for (int z = 0; z < dr; z++)
                for (int x = 0; x < dr; x++)
                    if (counts[z, x] > 0 && clear(originXZ + (x + 0.5f) * cell, originXZ + (z + 0.5f) * cell)) counts[z, x] = 0;
            data.SetDetailLayer(0, 0, l, counts);
        }
    }

    // Dirt along road shoulders on the field terrain (layer 1).
    static void PaintShoulders(TerrainData data, Grid grid, float originXZ)
    {
        int ar = data.alphamapResolution, layers = data.alphamapLayers;
        float cell = data.size.x / ar;
        var alpha = data.GetAlphamaps(0, 0, ar, ar);
        for (int z = 0; z < ar; z++)
            for (int x = 0; x < ar; x++)
            {
                int k = grid.Nearest(originXZ + (x + 0.5f) * cell, originXZ + (z + 0.5f) * cell);
                if (grid.dist[k] == float.MaxValue) continue;
                float dirt = 1f - Mathf.SmoothStep(0f, 1f, (grid.dist[k] - grid.halfWidth[k] - 1f) / 3f);
                if (dirt <= 0f) continue;
                for (int l = 0; l < layers; l++) alpha[z, x, l] *= 1f - dirt;
                alpha[z, x, 1] += dirt;
            }
        data.SetAlphamaps(0, 0, alpha);
    }

    // ---------- Road meshes ----------
    static void BuildRoadMeshes(List<RoadPath> roads, Transform root)
    {
        var parent = new GameObject("Roads").transform;
        parent.SetParent(root, false);
        var asphalt = Material("M_Road_Asphalt", new Color(0.2f, 0.2f, 0.21f), 0.15f);
        var dirt = Material("M_Road_Dirt", new Color(0.4f, 0.32f, 0.22f), 0.05f);
        for (int r = 0; r < roads.Count; r++)
        {
            var road = roads[r];
            // A little higher per road, so overlapping junctions never flicker.
            float lift = 0.1f + r * 0.01f;
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float along = 0f;
            for (int i = 0; i < road.points.Count; i++)
            {
                Vector2 p = road.points[i];
                Vector2 dir = (road.points[Mathf.Min(i + 1, road.points.Count - 1)] - road.points[Mathf.Max(i - 1, 0)]).normalized;
                Vector2 side = new Vector2(dir.y, -dir.x) * road.HalfWidth;
                if (i > 0) along += Vector2.Distance(p, road.points[i - 1]);
                float y = road.heights[i] + lift;
                verts.Add(new Vector3(p.x - side.x, y, p.y - side.y));
                verts.Add(new Vector3(p.x + side.x, y, p.y + side.y));
                float v = along / road.def.width;
                uvs.Add(new Vector2(0f, v));
                uvs.Add(new Vector2(1f, v));
                if (i == 0) continue;
                int b = verts.Count - 4;
                tris.AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
            }
            var mesh = new Mesh { name = road.def.name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            string meshPath = $"{Dir}/Road_{road.def.name.Replace(' ', '_')}.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            // Visual only: the terrain underneath is cut to the road's level and carries the wheels.
            var go = new GameObject(road.def.name);
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = road.def.paved ? asphalt : dirt;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
        }
    }

    // ---------- Locations ----------
    static void BuildLocations(Terrain world, List<RoadPath> roads, Transform root, System.Random rng)
    {
        var parent = new GameObject("Locations").transform;
        parent.SetParent(root, false);
        var walls = new[] { Material("M_Building_Concrete", new Color(0.58f, 0.56f, 0.52f), 0.1f), Material("M_Building_Wood", new Color(0.45f, 0.34f, 0.24f), 0.05f) };
        var roof = Material("M_Building_Roof", new Color(0.26f, 0.24f, 0.23f), 0.1f);
        var signWood = Material("M_Sign", new Color(0.36f, 0.27f, 0.18f), 0.05f);

        foreach (var loc in Locations)
        {
            var site = new GameObject(loc.name).transform;
            site.SetParent(parent, false);
            float ground = LocationHeight(loc);
            site.position = new Vector3(loc.position.x, ground, loc.position.y);

            // Placeholder buildings: boxes with a roof slab, kept off the roads and apart from each other.
            var placed = new List<Rect>();
            int made = 0;
            for (int attempt = 0; attempt < 200 && made < loc.buildings; attempt++)
            {
                float ang = (float)rng.NextDouble() * Mathf.PI * 2f, rad = Mathf.Sqrt((float)rng.NextDouble()) * loc.radius * 0.75f;
                var c = loc.position + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * rad;
                float w = 6f + (float)rng.NextDouble() * 6f, d = 5f + (float)rng.NextDouble() * 5f;
                float clearance = Mathf.Max(w, d) * 0.75f;
                if (DistanceToRoads(c, roads) < clearance + 4f) continue;
                var r = new Rect(c.x - clearance - 2f, c.y - clearance - 2f, (clearance + 2f) * 2f, (clearance + 2f) * 2f);
                if (placed.Exists(o => o.Overlaps(r))) continue;
                placed.Add(r);
                float h = 3f + (float)rng.NextDouble() * 2.5f;
                var building = new GameObject($"Building_{++made}").transform;
                building.SetParent(site, false);
                building.position = new Vector3(c.x, ground, c.y);
                building.rotation = Quaternion.Euler(0f, rng.Next(0, 4) * 90f + Jitter(rng, 10f), 0f);
                Box("Walls", building, new Vector3(0f, h / 2f, 0f), new Vector3(w, h, d), walls[rng.Next(walls.Length)]);
                Box("Roof", building, new Vector3(0f, h + 0.15f, 0f), new Vector3(w + 0.6f, 0.3f, d + 0.6f), roof);
            }

            PlaceSign(loc, roads, site, world, signWood);
        }
    }

    // A name board beside the road where it enters the clearing.
    static void PlaceSign(Location loc, List<RoadPath> roads, Transform site, Terrain world, Material wood)
    {
        Vector2 best = loc.position + Vector2.right * loc.radius, dir = Vector2.right;
        float bestErr = float.MaxValue, halfWidth = 3f;
        foreach (var road in roads)
            for (int i = 1; i < road.points.Count; i++)
            {
                float err = Mathf.Abs(Vector2.Distance(road.points[i], loc.position) - (loc.radius + 6f));
                if (err >= bestErr) continue;
                bestErr = err;
                best = road.points[i];
                dir = (road.points[i] - road.points[i - 1]).normalized;
                halfWidth = road.HalfWidth;
            }
        Vector2 side = new Vector2(dir.y, -dir.x);
        Vector2 spot = best + side * (halfWidth + 2f);
        var sign = new GameObject("Sign").transform;
        sign.SetParent(site, false);
        float ground = world.SampleHeight(new Vector3(spot.x, 0f, spot.y)) + world.transform.position.y;
        sign.position = new Vector3(spot.x, ground, spot.y);
        // Face along the road, so it reads from both directions of travel.
        sign.rotation = Quaternion.LookRotation(new Vector3(dir.x, 0f, dir.y));
        Box("Post", sign, new Vector3(0f, 1.1f, 0f), new Vector3(0.15f, 2.2f, 0.15f), wood);
        Box("Board", sign, new Vector3(0f, 2.1f, 0f), new Vector3(2.8f, 0.8f, 0.08f), wood);
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        foreach (var yaw in new[] { 0f, 180f })
        {
            var text = new GameObject("Text").AddComponent<TextMesh>();
            text.transform.SetParent(sign, false);
            text.transform.localPosition = new Vector3(0f, 2.1f, yaw == 0f ? -0.05f : 0.05f);
            text.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            text.text = loc.name.ToUpperInvariant();
            text.font = font;
            text.GetComponent<MeshRenderer>().sharedMaterial = font.material;
            text.fontSize = 48;
            text.characterSize = 0.05f;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.color = new Color(0.95f, 0.92f, 0.8f);
            text.gameObject.AddComponent<WorldText>().shader = Shader.Find("ProjectZed/WorldText");
        }
    }

    static float DistanceToRoads(Vector2 p, List<RoadPath> roads)
    {
        float best = float.MaxValue;
        foreach (var road in roads)
            foreach (var q in road.points)
                best = Mathf.Min(best, Vector2.Distance(p, q) - road.HalfWidth);
        return best;
    }

    // ---------- Boundary & field ----------
    static void BuildBoundary(Transform root)
    {
        var parent = new GameObject("Boundary").transform;
        parent.SetParent(root, false);
        float e = WorldSize * 0.5f - BoundaryInset;
        foreach (var (pos, size) in new[]
        {
            (new Vector3(0f, 50f, e), new Vector3(WorldSize, 120f, 1f)), (new Vector3(0f, 50f, -e), new Vector3(WorldSize, 120f, 1f)),
            (new Vector3(e, 50f, 0f), new Vector3(1f, 120f, WorldSize)), (new Vector3(-e, 50f, 0f), new Vector3(1f, 120f, WorldSize)),
        })
        {
            var w = new GameObject("Wall");
            w.transform.SetParent(parent, false);
            w.transform.position = pos;
            w.AddComponent<BoxCollider>().size = size;
        }
    }

    // The field's own invisible walls would stop anyone leaving, and rocks the roads pass through would block them.
    // Both are switched off (not deleted), so Build Forest Field's objects are still there.
    static void HideFieldObstacles(List<RoadPath> roads)
    {
        var fieldBoundary = FindRoot("Boundary");
        if (fieldBoundary != null) fieldBoundary.SetActive(false);
        var rocks = FindRoot("Rocks");
        if (rocks == null) return;
        foreach (Transform rock in rocks.transform)
        {
            var p = new Vector2(rock.position.x, rock.position.z);
            float size = 1f;
            var r = rock.GetComponentInChildren<Renderer>(true);
            if (r != null) size = Mathf.Max(r.bounds.extents.x, r.bounds.extents.z);
            rock.gameObject.SetActive(DistanceToRoads(p, roads) > size + 1.5f);
        }
    }

    static GameObject FindRoot(string name)
    {
        foreach (var go in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            if (go.name == name) return go;
        return null;
    }

    // ---------- Helpers ----------
    static void Box(string name, Transform parent, Vector3 localPos, Vector3 size, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = size;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic);
    }

    static float Jitter(System.Random rng, float amount) => ((float)rng.NextDouble() * 2f - 1f) * amount;

    static Material Material(string name, Color color, float smoothness)
    {
        string path = $"{Dir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", color);
        m.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(m);
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
