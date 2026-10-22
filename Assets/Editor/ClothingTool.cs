using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Characters > Clothing: puts garments built by Blender/Scripts/make_clothes.py on every base-male character
// in the scene (player, NPCs, BaseMale_Mannequin). Each garment FBX carries the base male's skeleton, so its meshes
// are bound to each character's own bones by name. Garments live as "Clothing_<Name>" children of the character's
// model; while any of them covers the groin, the genitals mesh is hidden. Re-run after re-exporting a garment.
public static class ClothingTool
{
    const string ClothingDir = "Assets/Characters/Clothing";
    const string Genitals = "BaseMale_Genitals";
    const string Prefix = "Clothing_";

    // Garment name -> covers the groin (hide the genitals while worn).
    static readonly (string name, bool coversGroin)[] Garments = { ("HeartBoxers", true) };

    [MenuItem("Tools/Characters/Clothing/Wear Heart Boxers (All Characters)")]
    public static void WearHeartBoxers() => WearOnAll("HeartBoxers");

    [MenuItem("Tools/Characters/Clothing/Undress All Characters")]
    public static void UndressAll()
    {
        foreach (var model in Characters())
        {
            foreach (var t in model.Cast<Transform>().Where(t => t.name.StartsWith(Prefix)).ToArray())
                Undo.DestroyObjectImmediate(t.gameObject);
            UpdateGenitals(model);
        }
        Save();
    }

    public static void WearOnAll(string garment)
    {
        var fbx = $"{ClothingDir}/{garment}/{garment}.fbx";
        var source = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
        if (source == null) { Debug.LogError("[ClothingTool] Missing " + fbx); return; }
        var material = GarmentMaterial(garment);
        int dressed = 0;
        foreach (var model in Characters())
        {
            var old = model.Find(Prefix + garment);
            if (old) Undo.DestroyObjectImmediate(old.gameObject);
            var bones = model.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
            var skin = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).First(r => r.name == "BaseMale_Body");
            var holder = new GameObject(Prefix + garment) { layer = skin.gameObject.layer };
            Undo.RegisterCreatedObjectUndo(holder, "Wear " + garment);
            holder.transform.SetParent(model, false);
            foreach (var src in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var missing = src.bones.Where(b => !bones.ContainsKey(b.name)).Select(b => b.name).ToArray();
                if (missing.Length > 0) { Debug.LogError($"[ClothingTool] {model.parent?.name}: no bones {string.Join(", ", missing)}"); continue; }
                var go = new GameObject(src.name) { layer = holder.layer };
                go.transform.SetParent(holder.transform, false);
                var r = go.AddComponent<SkinnedMeshRenderer>();
                r.sharedMesh = src.sharedMesh;
                r.bones = src.bones.Select(b => bones[b.name]).ToArray();
                r.rootBone = bones[src.rootBone ? src.rootBone.name : skin.rootBone.name];
                r.localBounds = skin.localBounds;
                r.sharedMaterial = material;
                r.shadowCastingMode = skin.shadowCastingMode;
                r.receiveShadows = skin.receiveShadows;
            }
            UpdateGenitals(model);
            dressed++;
        }
        Save();
        Debug.Log($"[ClothingTool] {garment} on {dressed} characters");
    }

    // The character models wearing the base male: their own model root (the object holding the Animator).
    static Transform[] Characters() =>
        Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)
            .Where(r => r.name == "BaseMale_Body").Select(r => r.transform.parent).Distinct().ToArray();

    static void UpdateGenitals(Transform model)
    {
        var genitals = model.Find(Genitals);
        if (genitals == null) return;
        bool covered = model.Cast<Transform>().Any(t => t.name.StartsWith(Prefix) &&
                                                        Garments.Any(g => Prefix + g.name == t.name && g.coversGroin));
        var r = genitals.GetComponent<Renderer>();
        Undo.RecordObject(r, "Clothing");
        r.enabled = !covered;
    }

    static Material GarmentMaterial(string garment)
    {
        var path = $"{ClothingDir}/{garment}/{garment}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        // ProjectZed/WindLit in cloth mode: fabric ripples outward in the wind (GPU, driven by WindManager).
        var shader = Shader.Find("ProjectZed/WindLit");
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
        if (mat.shader != shader) mat.shader = shader;
        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>($"{ClothingDir}/{garment}/{garment}_Albedo.png"));
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Smoothness", 0.1f);  // matte cotton
        mat.EnableKeyword("_WIND_CLOTH");
        // y: drift downwind, z: ripple height (metres at full storm). Tight garments stay small; loose ones can go higher.
        mat.SetVector("_WindParams", new Vector4(1f, 0.004f, 0.006f, 0f));
        mat.SetVector("_WindParams2", new Vector4(1f, 9f, 0f, 0f));
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void Save()
    {
        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }
}
