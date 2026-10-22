using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Environment > Build Houses: swaps WorldBuilder's placeholder boxes (World/Locations/<site>/Building_N)
// for real houses with interiors. Run it after Build World and Build PNW Forest; re-running rebuilds every house.
//
// The houses come from Blender (Blender/Scripts/make_house.py, floor plans in house_plans.py):
//   Assets/Environment/Houses/Models/House_<Type>.fbx  Exterior / Interior / Ramps meshes, material slots by name
//   Assets/Environment/Houses/Plans/House_<Type>.json  doors, window panes, rooms, furniture (house-local coords)
//   Assets/Environment/Houses/Models/House_Kit.fbx     door slabs and furniture
// Each placeholder gets the biggest house type that fits its clearance (from the site's list below), faces the
// nearest road, and rolls its own paint, finishes, furniture and a few already-broken windows. Terrain trees and
// plants inside each house's footprint are cleared. Textures in Assets/Environment/Houses/Textures
// (<set>_color.jpg, _normal.jpg, _mask.png) are used when present; otherwise materials are flat colours.
public static class HouseBuilder
{
    const string Root = "Assets/Environment/Houses";
    const string ModelDir = Root + "/Models", PlanDir = Root + "/Plans", MatDir = Root + "/Materials", TexDir = Root + "/Textures";
    const int Seed = 1987;
    const float BrokenWindowChance = 0.08f, ShackBrokenWindowChance = 0.35f, OpenInteriorDoorChance = 0.4f;

    // Which house types each site prefers, most wanted first (shuffled per house, so these are weights).
    static readonly Dictionary<string, string[]> SiteTypes = new Dictionary<string, string[]>
    {
        ["North Village"] = new[] { "Rambler", "Rambler", "Farmhouse", "Cabin" },
        ["Southfield"] = new[] { "Rambler", "Farmhouse", "Rambler", "Cabin" },
        ["East Farm"] = new[] { "Farmhouse", "Farmhouse", "Cabin", "Shack" },
        ["NE Outpost"] = new[] { "Cabin", "Shack", "Cabin" },
        ["SW Camp"] = new[] { "Shack", "Cabin", "Shack" },
        ["West Quarry"] = new[] { "Shack", "Cabin" },
    };
    static readonly string[] DefaultTypes = { "Rambler", "Cabin" };
    static readonly string[] AllTypes = { "Shack", "Cabin", "Farmhouse", "Rambler" };

    // ---------- Plan JSON ----------
    [Serializable] class Plan { public string name; public float[] size; public float floor; public int storeys; public float storeyHeight, ceiling, ridge; public DoorDef[] doors; public PaneDef[] panes; public RoomDef[] rooms; public FurnDef[] furniture; }
    [Serializable] class DoorDef { public float[] pos; public float width, height, yaw, thickness; public bool exterior, hasDoor; public int storey; }
    [Serializable] class PaneDef { public float[] pos; public float[] size; public float yaw; public bool exterior; }
    [Serializable] class RoomDef { public string name, type; public int storey; public float[] min, max; }
    [Serializable] class FurnDef { public string item; public float[] pos; public float yaw, chance; public string room; }

    static Vector3 V(float[] a) => new Vector3(a[0], a[1], a[2]);

    class HouseType
    {
        public string name;
        public Plan plan;
        public Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
        public Dictionary<string, string[]> slots = new Dictionary<string, string[]>();   // object -> material slot per submesh
        public Rect footprint;   // house-local xz incl. porch, steps, chimney
        public float radius;
    }

    class Kit
    {
        public Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
        public Dictionary<string, string[]> slots = new Dictionary<string, string[]>();
        public Dictionary<string, Material> imported = new Dictionary<string, Material>();   // Imp_* slots keep their own (textured) material
        public List<Alternate> alternates = new List<Alternate>();
    }

    // A piece from the BlendSwap packs (Blender/Scripts/import_furniture.py) that can stand in for a kit item.
    [Serializable] class Alternate { public string item, replaces; public string[] types; public float chance; }
    [Serializable] class AlternateList { public Alternate[] items; }

    // ---------- Materials ----------
    // slot -> variants: (texture set, metres per texture repeat, tint, smoothness, metallic)
    struct MatDef
    {
        public string tex; public float tile; public Color tint; public float smooth, metal;
        public MatDef(string tex, float tile, Color tint, float smooth = 0.2f, float metal = 0f) { this.tex = tex; this.tile = tile; this.tint = tint; this.smooth = smooth; this.metal = metal; }
    }
    static Color C(float r, float g, float b, float a = 1f) => new Color(r, g, b, a);

    static readonly Dictionary<string, (string name, MatDef def)[]> Variants = new Dictionary<string, (string, MatDef)[]>
    {
        ["Ext_Siding"] = new[]
        {
            ("BlueGrey", new MatDef("white_planks_clean", 1.8f, C(0.55f, 0.62f, 0.68f))),
            ("Sage", new MatDef("white_planks_clean", 1.8f, C(0.6f, 0.66f, 0.53f))),
            ("Cream", new MatDef("white_planks_clean", 1.8f, C(0.88f, 0.83f, 0.68f))),
            ("BarnRed", new MatDef("white_planks_clean", 1.8f, C(0.55f, 0.21f, 0.17f))),
            ("Brown", new MatDef("white_planks_clean", 1.8f, C(0.47f, 0.36f, 0.26f))),
            ("Mustard", new MatDef("white_planks_clean", 1.8f, C(0.76f, 0.62f, 0.33f))),
            ("White", new MatDef("white_planks_clean", 1.8f, C(0.9f, 0.9f, 0.87f))),
            ("Weathered", new MatDef("weathered_plank_siding", 1.57f, C(0.95f, 0.93f, 0.9f))),
        },
        ["Ext_Siding_Shack"] = new[] { ("Planks", new MatDef("wood_plank_wall", 1f, C(0.8f, 0.78f, 0.75f))) },
        ["Ext_Trim"] = new[]
        {
            ("White", new MatDef(null, 1f, C(0.9f, 0.9f, 0.86f), 0.35f)),
            ("Cream", new MatDef(null, 1f, C(0.86f, 0.82f, 0.7f), 0.35f)),
            ("DarkBrown", new MatDef(null, 1f, C(0.28f, 0.2f, 0.14f), 0.3f)),
        },
        ["Ext_Roof"] = new[]
        {
            ("Shingle", new MatDef("grey_roof_01", 8f, C(0.85f, 0.85f, 0.85f), 0.15f)),
            ("ShingleBrown", new MatDef("roof_07", 2f, C(0.9f, 0.85f, 0.8f), 0.15f)),
        },
        ["Ext_Roof_Shack"] = new[] { ("Tin", new MatDef("rusty_corrugated_iron", 2f, C(1f, 1f, 1f), 0.35f, 0.4f)) },
        ["Ext_Foundation"] = new[] { ("Concrete", new MatDef("concrete_floor_worn_001", 3f, C(0.85f, 0.84f, 0.82f), 0.1f)) },
        ["Ext_Deck"] = new[] { ("Deck", new MatDef("wood_floor_deck", 1.8f, C(0.8f, 0.85f, 0.95f), 0.15f)) },
        ["Ext_WindowFrame"] = new[] { ("Aluminium", new MatDef(null, 1f, C(0.72f, 0.72f, 0.7f), 0.55f, 0.7f)) },
        ["Ext_Brick"] = new[] { ("Brick", new MatDef(null, 1f, C(0.36f, 0.19f, 0.15f), 0.1f)) },
        ["Ext_Metal"] = new[] { ("Stovepipe", new MatDef(null, 1f, C(0.12f, 0.12f, 0.12f), 0.4f, 0.6f)) },
        ["Int_Plaster"] = new[]
        {
            ("OffWhite", new MatDef("painted_plaster_wall", 2f, C(0.9f, 0.88f, 0.82f), 0.25f)),
            ("PaleYellow", new MatDef("painted_plaster_wall", 2f, C(0.92f, 0.86f, 0.66f), 0.25f)),
            ("PaleBlue", new MatDef("painted_plaster_wall", 2f, C(0.74f, 0.8f, 0.84f), 0.25f)),
            ("PaleGreen", new MatDef("painted_plaster_wall", 2f, C(0.76f, 0.83f, 0.72f), 0.25f)),
        },
        ["Int_Wallpaper"] = new[]
        {
            ("Tan", new MatDef("decrepit_wallpaper", 2.5f, C(1f, 0.95f, 0.85f), 0.15f)),
            ("Rose", new MatDef("decrepit_wallpaper", 2.5f, C(0.95f, 0.8f, 0.78f), 0.15f)),
            ("Olive", new MatDef("decrepit_wallpaper", 2.5f, C(0.82f, 0.85f, 0.68f), 0.15f)),
        },
        ["Int_Tile"] = new[]
        {
            ("White", new MatDef("square_tiled_wall", 2f, C(0.95f, 0.95f, 0.93f), 0.6f)),
            ("Mint", new MatDef("square_tiled_wall", 2f, C(0.75f, 0.9f, 0.82f), 0.6f)),
            ("Pink", new MatDef("square_tiled_wall", 2f, C(0.95f, 0.78f, 0.8f), 0.6f)),
        },
        ["Int_Plank"] = new[] { ("Plank", new MatDef("wood_plank_wall", 1f, C(0.95f, 0.88f, 0.78f), 0.15f)) },
        ["Int_Trim"] = new[] { ("White", new MatDef(null, 1f, C(0.9f, 0.89f, 0.85f), 0.45f)) },
        ["Int_Ceiling"] = new[] { ("White", new MatDef("painted_plaster_wall", 2f, C(0.93f, 0.92f, 0.89f), 0.1f)) },
        ["Int_Stair"] = new[] { ("Oak", new MatDef("old_wood_floor", 3f, C(0.9f, 0.85f, 0.8f), 0.3f)) },
        ["Floor_Wood"] = new[] { ("Oak", new MatDef("old_wood_floor", 3f, C(0.9f, 0.85f, 0.8f), 0.3f)) },
        ["Floor_Carpet"] = new[]
        {
            ("Tan", new MatDef("dirty_carpet", 1.2f, C(0.85f, 0.75f, 0.6f), 0.05f)),
            ("Brown", new MatDef("dirty_carpet", 1.2f, C(0.6f, 0.45f, 0.32f), 0.05f)),
            ("Blue", new MatDef("dirty_carpet", 1.2f, C(0.55f, 0.6f, 0.7f), 0.05f)),
            ("Mauve", new MatDef("dirty_carpet", 1.2f, C(0.7f, 0.55f, 0.6f), 0.05f)),
            ("Green", new MatDef("dirty_carpet", 1.2f, C(0.55f, 0.6f, 0.42f), 0.05f)),
        },
        ["Floor_Lino"] = new[] { ("Lino", new MatDef("old_linoleum_flooring_01", 2f, C(1f, 1f, 1f), 0.35f)) },
        ["Furn_Wood"] = new[] { ("Oak", new MatDef(null, 1f, C(0.5f, 0.34f, 0.2f), 0.35f)), ("Pine", new MatDef(null, 1f, C(0.68f, 0.5f, 0.3f), 0.35f)) },
        ["Furn_DarkWood"] = new[] { ("Walnut", new MatDef(null, 1f, C(0.28f, 0.19f, 0.12f), 0.35f)) },
        ["Furn_Fabric"] = new[]
        {
            ("Tan", new MatDef(null, 1f, C(0.6f, 0.5f, 0.36f), 0.05f)),
            ("Olive", new MatDef(null, 1f, C(0.42f, 0.42f, 0.25f), 0.05f)),
            ("Rust", new MatDef(null, 1f, C(0.55f, 0.3f, 0.18f), 0.05f)),
            ("Blue", new MatDef(null, 1f, C(0.3f, 0.36f, 0.48f), 0.05f)),
        },
        ["Furn_Blanket"] = new[]
        {
            ("Navy", new MatDef(null, 1f, C(0.2f, 0.25f, 0.4f), 0.05f)),
            ("Maroon", new MatDef(null, 1f, C(0.45f, 0.15f, 0.15f), 0.05f)),
            ("Green", new MatDef(null, 1f, C(0.28f, 0.4f, 0.28f), 0.05f)),
        },
        ["Furn_Mattress"] = new[] { ("White", new MatDef(null, 1f, C(0.88f, 0.86f, 0.8f), 0.05f)) },
        ["Furn_White"] = new[] { ("Enamel", new MatDef(null, 1f, C(0.9f, 0.89f, 0.85f), 0.6f)), ("Almond", new MatDef(null, 1f, C(0.88f, 0.82f, 0.68f), 0.6f)) },
        ["Furn_Metal"] = new[] { ("Steel", new MatDef(null, 1f, C(0.62f, 0.62f, 0.62f), 0.55f, 0.8f)) },
        ["Furn_Black"] = new[] { ("Black", new MatDef(null, 1f, C(0.05f, 0.05f, 0.05f), 0.4f)) },
        ["Furn_Porcelain"] = new[] { ("Porcelain", new MatDef(null, 1f, C(0.94f, 0.94f, 0.92f), 0.8f)) },
        ["Furn_Counter"] = new[] { ("Harvest", new MatDef(null, 1f, C(0.8f, 0.72f, 0.5f), 0.5f)), ("Grey", new MatDef(null, 1f, C(0.75f, 0.74f, 0.7f), 0.5f)) },
        ["Furn_Rug"] = new[] { ("Red", new MatDef(null, 1f, C(0.5f, 0.22f, 0.18f), 0.05f)), ("Brown", new MatDef(null, 1f, C(0.4f, 0.3f, 0.2f), 0.05f)) },
        ["Furn_Leather"] = new[] { ("Oxblood", new MatDef(null, 1f, C(0.3f, 0.12f, 0.09f), 0.45f)), ("Brown", new MatDef(null, 1f, C(0.28f, 0.18f, 0.11f), 0.45f)) },
        ["Furn_Mirror"] = new[] { ("Mirror", new MatDef(null, 1f, C(0.6f, 0.66f, 0.7f), 0.95f, 1f)) },
        ["Door_Paint"] = new[] { ("White", new MatDef(null, 1f, C(0.88f, 0.87f, 0.83f), 0.4f)), ("Brown", new MatDef(null, 1f, C(0.4f, 0.27f, 0.17f), 0.4f)) },
        ["Door_Brass"] = new[] { ("Brass", new MatDef(null, 1f, C(0.75f, 0.6f, 0.3f), 0.7f, 1f)) },
    };

    // Rough average colour of each texture set, used (times the tint) while its textures aren't in the project.
    static readonly Dictionary<string, Color> FallbackAlbedo = new Dictionary<string, Color>
    {
        ["white_planks_clean"] = C(0.92f, 0.92f, 0.9f), ["weathered_plank_siding"] = C(0.55f, 0.5f, 0.45f),
        ["wood_plank_wall"] = C(0.5f, 0.4f, 0.3f), ["grey_roof_01"] = C(0.24f, 0.24f, 0.25f), ["roof_07"] = C(0.3f, 0.26f, 0.23f),
        ["rusty_corrugated_iron"] = C(0.45f, 0.3f, 0.2f), ["concrete_floor_worn_001"] = C(0.55f, 0.54f, 0.52f),
        ["wood_floor_deck"] = C(0.45f, 0.35f, 0.26f), ["painted_plaster_wall"] = C(0.92f, 0.92f, 0.9f),
        ["decrepit_wallpaper"] = C(0.75f, 0.68f, 0.55f), ["square_tiled_wall"] = C(0.92f, 0.92f, 0.9f),
        ["old_wood_floor"] = C(0.5f, 0.36f, 0.23f), ["dirty_carpet"] = C(0.65f, 0.62f, 0.58f),
        ["old_linoleum_flooring_01"] = C(0.75f, 0.72f, 0.6f),
    };

    static readonly Dictionary<string, Material> matCache = new Dictionary<string, Material>();
    static Material glassMaterial;

    [MenuItem("Tools/Environment/Build Houses")]
    public static void Build()
    {
        if (EditorApplication.isPlaying) { Debug.LogError("[HouseBuilder] Stop Play mode first."); return; }
        matCache.Clear();
        ConfigureImporters();
        var types = LoadTypes();
        var kit = LoadKit();
        if (types.Count == 0 || kit == null) { Debug.LogError("[HouseBuilder] Run Blender/Scripts/make_house.py first."); return; }
        glassMaterial = GlassMaterial();

        var locations = GameObject.Find("World/Locations");
        if (locations == null) { Debug.LogError("[HouseBuilder] World/Locations not found: run Tools > Environment > Build World."); return; }
        var roadPoints = RoadPoints();
        var terrains = UnityEngine.Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude);
        var rng = new System.Random(Seed);
        var counts = new Dictionary<string, int>();
        int built = 0;

        foreach (Transform site in locations.transform)
        {
            var prefs = SiteTypes.TryGetValue(site.name, out var p) ? p : DefaultTypes;
            foreach (Transform building in site)
            {
                if (!building.name.StartsWith("Building_")) continue;
                for (int i = building.childCount - 1; i >= 0; i--)
                {
                    var child = building.GetChild(i);
                    if (child.name.StartsWith("House_")) UnityEngine.Object.DestroyImmediate(child.gameObject);
                    else child.gameObject.SetActive(false);   // placeholder Walls / Roof
                }
                var walls = building.Find("Walls");
                Vector3 size = walls != null ? walls.localScale : new Vector3(8f, 4f, 7f);
                float clearance = Mathf.Max(size.x, size.z) * 0.75f + 2f;
                Vector3 pos = building.position;
                Vector2 road = NearestRoad(roadPoints, new Vector2(pos.x, pos.z), out float roadDistance);
                // WorldBuilder kept a (clearance + 2) square around each placeholder free of the others
                float room = Mathf.Min(clearance + 2f, roadDistance - 2f);

                var type = PickType(prefs, types, room, rng);
                float yaw = Mathf.Atan2(road.x - pos.x, road.y - pos.z) * Mathf.Rad2Deg;
                var house = BuildHouse(type, kit, building, yaw, rng, site.name);
                ClearTerrain(terrains, house.transform, type.footprint);
                counts[type.name] = counts.TryGetValue(type.name, out int n) ? n + 1 : 1;
                built++;
            }
        }

        foreach (var t in terrains) EditorUtility.SetDirty(t.terrainData);
        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[HouseBuilder] Built {built} houses: " + string.Join(", ", counts.Select(kv => $"{kv.Value} {kv.Key}")));
    }

    // Command line: Unity -batchmode -quit -projectPath ... -executeMethod HouseBuilder.BuildFromCommandLine
    public static void BuildFromCommandLine()
    {
        var scene = EditorSceneManager.OpenScene("Assets/ProjectZed.unity");
        string backupDir = "SceneBackups";
        Directory.CreateDirectory(backupDir);
        string backup = $"{backupDir}/ProjectZed_before_Houses_{DateTime.Now:yyyyMMdd_HHmm}.unity";
        if (!Directory.GetFiles(backupDir, "ProjectZed_before_Houses_*").Any()) File.Copy(scene.path, backup);
        Build();
    }

    static HouseType PickType(string[] prefs, Dictionary<string, HouseType> types, float room, System.Random rng)
    {
        var order = prefs.OrderBy(_ => rng.Next()).ToList();
        foreach (var name in order)
            if (types.TryGetValue(name, out var t) && t.radius <= room) return t;
        // Nothing preferred fits: the biggest of any type that does, else the smallest.
        var fits = AllTypes.Where(types.ContainsKey).Select(n => types[n]).Where(t => t.radius <= room).OrderByDescending(t => t.radius).ToList();
        return fits.Count > 0 ? fits[0] : types.Values.OrderBy(t => t.radius).First();
    }

    // ---------- One house ----------
    static GameObject BuildHouse(HouseType type, Kit kit, Transform building, float yaw, System.Random rng, string site)
    {
        var plan = type.plan;
        bool shack = type.name == "Shack";
        var root = new GameObject("House_" + type.name);
        root.transform.SetParent(building, false);
        root.transform.position = building.position;
        root.transform.rotation = Quaternion.Euler(0f, yaw, 0f);

        // This house's look: one pick per material slot.
        var look = new Dictionary<string, Material>();
        Material Pick(string slot)
        {
            if (look.TryGetValue(slot, out var m)) return m;
            if (slot.StartsWith("Imp_") && kit.imported.TryGetValue(slot, out var own)) return own;
            if (slot == "Ext_Soffit") return look[slot] = Pick("Ext_Trim");
            string key = slot;
            if (shack && (slot == "Ext_Siding" || slot == "Ext_Roof")) key = slot + "_Shack";
            if (!Variants.TryGetValue(key, out var vars)) key = "Int_Trim";
            vars = Variants[key];
            int i = rng.Next(vars.Length);
            if (key == "Ext_Siding" && type.name != "Cabin" && vars[i].name == "Weathered") i = rng.Next(vars.Length - 1);
            if (key == "Ext_Siding" && type.name == "Cabin" && rng.NextDouble() < 0.6) i = vars.Length - 1;   // cabins: mostly bare boards
            m = GetMaterial(key, vars[i].name, vars[i].def);
            look[slot] = m;
            if (slot == "Ext_Trim") look["Ext_Soffit"] = m;
            return m;
        }
        if (type.name == "Cabin" || shack) look["Ext_Trim"] = look["Ext_Soffit"] = GetMaterial("Ext_Trim", "DarkBrown", Variants["Ext_Trim"][2].def);

        var lod0 = new List<Renderer>();
        var lod1 = new List<Renderer>();
        foreach (var part in new[] { "Exterior", "Interior", "Ramps" })
        {
            if (!type.meshes.TryGetValue(part, out var mesh)) continue;
            var go = new GameObject(part);
            go.transform.SetParent(root.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
            if (part == "Ramps")
            {
                go.layer = 2;   // Ignore Raycast: invisible slopes over the steps
                continue;
            }
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = type.slots[part].Select(Pick).ToArray();
            go.AddComponent<SurfaceTag>().surface = SurfaceType.Wood;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
            r.receiveGI = ReceiveGI.LightProbes;   // lit by the baked probe volumes (Bake House Lighting), no lightmaps
            lod0.Add(r);
            if (part == "Exterior") lod1.Add(r);
        }

        // Doors
        foreach (var d in plan.doors)
        {
            if (!d.hasDoor) continue;
            string item = d.exterior ? "door_exterior" : "door_interior";
            var pivot = new GameObject("Door");
            pivot.transform.SetParent(root.transform, false);
            var rot = Quaternion.Euler(0f, d.yaw, 0f);
            pivot.transform.localRotation = rot;
            pivot.transform.localPosition = V(d.pos) - rot * Vector3.right * (d.width / 2f);
            var door = pivot.AddComponent<Door>();
            door.openSound = LoadEvent("Door_Open");
            door.closeSound = LoadEvent("Door_Close");
            if (!d.exterior && rng.NextDouble() < OpenInteriorDoorChance) door.startAngle = (rng.NextDouble() < 0.5 ? 1 : -1) * 80f;
            var slab = new GameObject("Slab");
            slab.transform.SetParent(pivot.transform, false);
            slab.transform.localPosition = new Vector3(0.005f, 0.005f, 0f);
            slab.transform.localScale = new Vector3(d.width - 0.01f, d.height - 0.01f, 1f);
            slab.AddComponent<MeshFilter>().sharedMesh = kit.meshes[item];
            var r = slab.AddComponent<MeshRenderer>();
            r.sharedMaterials = kit.slots[item].Select(Pick).ToArray();
            slab.AddComponent<BoxCollider>();
            slab.AddComponent<SurfaceTag>().surface = SurfaceType.Wood;
            lod0.Add(r);
            if (d.exterior) lod1.Add(r);
        }

        // Window panes
        var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        float brokenChance = shack ? ShackBrokenWindowChance : BrokenWindowChance;
        foreach (var pn in plan.panes)
        {
            var go = new GameObject("Window");
            go.transform.SetParent(root.transform, false);
            go.transform.localPosition = V(pn.pos);
            go.transform.localRotation = Quaternion.Euler(0f, pn.yaw, 0f);
            go.transform.localScale = new Vector3(pn.size[0], pn.size[1], 0.006f);
            go.AddComponent<MeshFilter>().sharedMesh = cube;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = glassMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.AddComponent<BoxCollider>();
            go.AddComponent<SurfaceTag>().surface = SurfaceType.Glass;
            var glass = go.AddComponent<BreakableGlass>();
            glass.breakSound = LoadEvent("Glass_Break");
            glass.startBroken = rng.NextDouble() < brokenChance;
            lod0.Add(r);
            if (pn.exterior) lod1.Add(r);
        }

        // Furniture
        var furniture = new GameObject("Furniture").transform;
        furniture.SetParent(root.transform, false);
        foreach (var f in plan.furniture)
        {
            if (rng.NextDouble() > f.chance) continue;
            string item = f.item;
            foreach (var alt in kit.alternates.Where(a => a.replaces == f.item && a.types.Contains(type.name)).OrderBy(_ => rng.Next()))
                if (kit.meshes.ContainsKey(alt.item) && rng.NextDouble() < alt.chance) { item = alt.item; break; }
            if (!kit.meshes.TryGetValue(item, out var mesh)) continue;
            var go = new GameObject(item);
            go.transform.SetParent(furniture, false);
            go.transform.localPosition = V(f.pos);
            go.transform.localRotation = Quaternion.Euler(0f, f.yaw, 0f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterials = kit.slots[item].Select(Pick).ToArray();
            if (item != "rug") go.AddComponent<MeshCollider>().sharedMesh = mesh;
            bool metal = item is "fridge" or "stove" or "washer" or "dryer" or "shelf_metal" or "woodstove" or "bed_iron";
            go.AddComponent<SurfaceTag>().surface = metal ? SurfaceType.Metal : SurfaceType.Wood;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.BatchingStatic | StaticEditorFlags.ContributeGI);
            r.receiveGI = ReceiveGI.LightProbes;   // lit by the baked probe volumes (Bake House Lighting), no lightmaps
            lod0.Add(r);
        }

        // Rooms, for indoor audio
        var h = root.AddComponent<House>();
        h.type = type.name;
        h.rooms = plan.rooms.Select(rm =>
        {
            var b = new Bounds();
            b.SetMinMax(V(rm.min) - new Vector3(0f, 0.1f, 0f), V(rm.max) + new Vector3(0f, 0.3f, 0f));
            return b;
        }).ToArray();
        h.radius = type.radius;

        // Inside (and furniture) drops out at a distance; the shell lasts until it's a few pixels.
        var lodGroup = root.AddComponent<LODGroup>();
        lodGroup.SetLODs(new[] { new LOD(0.09f, lod0.ToArray()), new LOD(0.012f, lod1.ToArray()) });
        lodGroup.RecalculateBounds();
        return root;
    }

    // ---------- Loading ----------
    static void ConfigureImporters()
    {
        foreach (var path in Directory.GetFiles(ModelDir, "House_*.fbx"))
        {
            var p = path.Replace('\\', '/');
            if (!(AssetImporter.GetAtPath(p) is ModelImporter mi)) continue;
            bool changed = false;
            if (!mi.isReadable) { mi.isReadable = true; changed = true; }
            if (mi.importAnimation) { mi.importAnimation = false; changed = true; }
            if (mi.animationType != ModelImporterAnimationType.None) { mi.animationType = ModelImporterAnimationType.None; changed = true; }
            if (mi.importNormals != ModelImporterNormals.Import) { mi.importNormals = ModelImporterNormals.Import; changed = true; }
            if (changed) mi.SaveAndReimport();
        }
    }

    static Dictionary<string, HouseType> LoadTypes()
    {
        var types = new Dictionary<string, HouseType>();
        foreach (var name in AllTypes)
        {
            string json = $"{PlanDir}/House_{name}.json", fbx = $"{ModelDir}/House_{name}.fbx";
            if (!File.Exists(json) || !File.Exists(fbx)) continue;
            var t = new HouseType { name = name, plan = JsonUtility.FromJson<Plan>(File.ReadAllText(json)) };
            ReadModel(fbx, t.meshes, t.slots);
            if (!t.meshes.TryGetValue("Exterior", out var ext)) continue;
            var b = ext.bounds;
            t.footprint = Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z);
            t.radius = new[] { new Vector2(b.min.x, b.min.z), new Vector2(b.max.x, b.min.z), new Vector2(b.min.x, b.max.z), new Vector2(b.max.x, b.max.z) }.Max(v => v.magnitude);
            types[name] = t;
            Debug.Log($"[HouseBuilder] {name}: footprint {t.footprint}, radius {t.radius:0.0} m, {t.plan.doors.Length} doorways, {t.plan.panes.Length} panes");
        }
        return types;
    }

    static Kit LoadKit()
    {
        string fbx = $"{ModelDir}/House_Kit.fbx";
        if (!File.Exists(fbx)) return null;
        var kit = new Kit();
        ReadModel(fbx, kit.meshes, kit.slots);
        string imported = $"{ModelDir}/House_KitImported.fbx", alts = $"{PlanDir}/House_KitImported.json";
        if (File.Exists(imported) && File.Exists(alts))
        {
            ReadModel(imported, kit.meshes, kit.slots);
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(imported);
            foreach (var r in model.GetComponentsInChildren<MeshRenderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && m.name.StartsWith("Imp_")) kit.imported[m.name] = m;
            kit.alternates.AddRange(JsonUtility.FromJson<AlternateList>(File.ReadAllText(alts)).items);
        }
        return kit;
    }

    // Meshes by object name, and the material slot name of each submesh (from the importer's materials).
    static void ReadModel(string path, Dictionary<string, Mesh> meshes, Dictionary<string, string[]> slots)
    {
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        foreach (var mf in model.GetComponentsInChildren<MeshFilter>(true))
        {
            var r = mf.GetComponent<MeshRenderer>();
            meshes[mf.name] = mf.sharedMesh;
            slots[mf.name] = r != null ? r.sharedMaterials.Select(m => m != null ? m.name : "Int_Trim").ToArray() : new string[0];
        }
    }

    static SoundEvent LoadEvent(string name) => AssetDatabase.LoadAssetAtPath<SoundEvent>($"Assets/Audio/Events/{name}.asset");

    static Material GetMaterial(string slot, string variant, MatDef def)
    {
        string name = $"H_{slot}_{variant}";
        if (matCache.TryGetValue(name, out var cached)) return cached;
        EnsureFolder(MatDir);
        string path = $"{MatDir}/{name}.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }
        m.SetColor("_BaseColor", def.tint);
        m.SetFloat("_Smoothness", def.smooth);
        m.SetFloat("_Metallic", def.metal);
        Texture2D color = null, normal = null, mask = null;
        if (def.tex != null)
        {
            color = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{def.tex}_color.jpg");
            normal = LoadNormal($"{TexDir}/{def.tex}_normal.jpg");
            mask = AssetDatabase.LoadAssetAtPath<Texture2D>($"{TexDir}/{def.tex}_mask.png");
        }
        if (def.tex != null && color == null && FallbackAlbedo.TryGetValue(def.tex, out var avg)) m.SetColor("_BaseColor", def.tint * avg);
        m.SetTexture("_BaseMap", color);
        m.SetTextureScale("_BaseMap", Vector2.one / def.tile);
        m.SetTexture("_BumpMap", normal);
        SetKeyword(m, "_NORMALMAP", normal != null);
        m.SetTexture("_MetallicGlossMap", mask);
        SetKeyword(m, "_METALLICSPECGLOSSMAP", mask != null);
        m.SetTexture("_OcclusionMap", mask);
        SetKeyword(m, "_OCCLUSIONMAP", mask != null);
        if (mask != null) m.SetFloat("_Smoothness", 1f);   // the mask's alpha carries it
        EditorUtility.SetDirty(m);
        matCache[name] = m;
        return m;
    }

    static Texture2D LoadNormal(string path)
    {
        if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) return null;
        if (ti.textureType != TextureImporterType.NormalMap) { ti.textureType = TextureImporterType.NormalMap; ti.SaveAndReimport(); }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static void SetKeyword(Material m, string keyword, bool on)
    {
        if (on) m.EnableKeyword(keyword); else m.DisableKeyword(keyword);
    }

    static Material GlassMaterial()
    {
        EnsureFolder(MatDir);
        string path = $"{MatDir}/H_Glass.mat";
        var m = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (m == null)
        {
            m = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(m, path);
        }
        // URP Lit, transparent (alpha blend), glossy, faintly green-blue
        m.SetFloat("_Surface", 1f);
        m.SetFloat("_Blend", 0f);
        m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
        m.SetFloat("_DstBlendAlpha", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        m.SetFloat("_ZWrite", 0f);
        m.SetFloat("_Cull", 0f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        m.SetOverrideTag("RenderType", "Transparent");
        m.SetColor("_BaseColor", new Color(0.72f, 0.82f, 0.82f, 0.22f));
        m.SetFloat("_Smoothness", 0.95f);
        m.SetFloat("_Metallic", 0.1f);
        EditorUtility.SetDirty(m);
        return m;
    }

    // ---------- World ----------
    static List<Vector2> RoadPoints()
    {
        var pts = new List<Vector2>();
        var roads = GameObject.Find("World/Roads");
        if (roads == null) return pts;
        foreach (var mf in roads.GetComponentsInChildren<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;
            var tr = mf.transform;
            foreach (var v in mf.sharedMesh.vertices)
            {
                var w = tr.TransformPoint(v);
                pts.Add(new Vector2(w.x, w.z));
            }
        }
        return pts;
    }

    static Vector2 NearestRoad(List<Vector2> pts, Vector2 p, out float distance)
    {
        Vector2 best = p + Vector2.up;
        float bestD = float.MaxValue;
        foreach (var q in pts)
        {
            float d = (q - p).sqrMagnitude;
            if (d < bestD) { bestD = d; best = q; }
        }
        distance = pts.Count > 0 ? Mathf.Sqrt(bestD) : 100f;
        return best;
    }

    // Removes terrain trees and plants under a house (its footprint plus a margin).
    static void ClearTerrain(Terrain[] terrains, Transform house, Rect footprint)
    {
        const float margin = 1.5f;
        var r = Rect.MinMaxRect(footprint.xMin - margin, footprint.yMin - margin, footprint.xMax + margin, footprint.yMax + margin);
        bool Inside(Vector3 world)
        {
            var l = house.InverseTransformPoint(world);
            return r.Contains(new Vector2(l.x, l.z));
        }
        // World-space bounds of the rect
        var corners = new[] { new Vector3(r.xMin, 0, r.yMin), new Vector3(r.xMax, 0, r.yMin), new Vector3(r.xMin, 0, r.yMax), new Vector3(r.xMax, 0, r.yMax) }
            .Select(c => house.TransformPoint(c)).ToArray();
        float minX = corners.Min(c => c.x), maxX = corners.Max(c => c.x), minZ = corners.Min(c => c.z), maxZ = corners.Max(c => c.z);

        foreach (var t in terrains)
        {
            var data = t.terrainData;
            Vector3 origin = t.transform.position, size = data.size;
            if (maxX < origin.x || minX > origin.x + size.x || maxZ < origin.z || minZ > origin.z + size.z) continue;
            var trees = data.treeInstances;
            var keep = trees.Where(ti => !Inside(origin + Vector3.Scale(ti.position, size))).ToArray();
            if (keep.Length != trees.Length) data.SetTreeInstances(keep, true);

            int res = data.detailResolution;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((minX - origin.x) / size.x * res), 0, res - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt((maxX - origin.x) / size.x * res), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((minZ - origin.z) / size.z * res), 0, res - 1);
            int z1 = Mathf.Clamp(Mathf.CeilToInt((maxZ - origin.z) / size.z * res), 0, res - 1);
            int w = x1 - x0 + 1, hgt = z1 - z0 + 1;
            for (int layer = 0; layer < data.detailPrototypes.Length; layer++)
            {
                var map = data.GetDetailLayer(x0, z0, w, hgt, layer);
                bool any = false;
                for (int z = 0; z < hgt; z++)
                    for (int x = 0; x < w; x++)
                    {
                        if (map[z, x] == 0) continue;
                        var world = origin + new Vector3((x0 + x + 0.5f) / res * size.x, 0f, (z0 + z + 0.5f) / res * size.z);
                        if (!Inside(world)) continue;
                        map[z, x] = 0;
                        any = true;
                    }
                if (any) data.SetDetailLayer(x0, z0, layer, map);
            }
        }
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
