using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Characters > Apply Base Body to Player: dresses the player in the underwear base body built in Blender
// (Blender/Scripts/make_game_body.py, from BlendSwap asset 21626) and hides Ch17's construction outfit. Ch17's own
// head, hair and hands stay.
//
// Blender's FBX has its own units, axes and bone orientations, so its meshes can't be dropped onto the player's
// skeleton as they are. Instead each mesh is moved into the original Ch17_Body's mesh space and given the original
// FBX's bind poses (matched by bone name), then saved as a mesh asset. Safe to re-run after re-exporting from Blender.
// Tools > Characters > Restore Player Outfit undoes the swap.
public static class BaseBodyBuilder
{
    const string SourceFbx = "Assets/Characters/BaseBody/Ch17_BaseBody.fbx";
    const string OutDir = "Assets/Characters/BaseBody";
    static readonly string[] Parts = { "Base_Body", "Base_Underwear" };
    // Hidden while he wears the base body: the construction outfit, and his hair and beard (the base head is bald and
    // clean-shaven; hair styles come later, like outfits).
    static readonly string[] Outfit = { "Ch17_Helmet", "Ch17_Vest", "Ch17_Shirt", "Ch17_Pants", "Ch17_Boots", "Ch17_Hair" };

    // Textures baked by Blender/Scripts/bake_textures.py. The player's head and hands get their own copy of Ch17's
    // material using Ch17_Player_Diffuse (buzz cut, seams eased into the body's skin tone); the NPCs keep the original.
    const string SkinTexture = OutDir + "/Base_Skin_Albedo.png";
    const string ClothTexture = OutDir + "/Base_Underwear_Albedo.png";
    const string PlayerHeadTexture = OutDir + "/Ch17_Player_Diffuse.png";
    const string Ch17Material = "Assets/Characters/Mixamo/Materials/Ch17_body.mat";
    const string PlayerHeadMaterial = OutDir + "/Ch17_body_player.mat";

    [MenuItem("Tools/Characters/Apply Base Body to Player")]
    public static void Apply()
    {
        var body = PlayerBody();
        if (body == null) return;

        // Bind poses of the player's skeleton, by bone name, from every mesh in Ch17's original FBX.
        var bindposes = new Dictionary<string, Matrix4x4>();
        SkinnedMeshRenderer origCh17Body = null;
        foreach (var smr in Renderers(PlayerAnimationBuilder.ModelPath))
        {
            if (smr.name == "Ch17_Body") origCh17Body = smr;
            var bp = smr.sharedMesh.bindposes;
            for (int i = 0; i < smr.bones.Length; i++)
            {
                var name = smr.bones[i].name;
                if (bindposes.TryGetValue(name, out var seen) && !Close(seen, bp[i]))
                    Debug.LogWarning($"[BaseBodyBuilder] {smr.name} has a different bind pose for {name}");
                bindposes[name] = bp[i];
            }
        }

        var source = new Dictionary<string, SkinnedMeshRenderer>();
        foreach (var smr in Renderers(SourceFbx)) source[smr.name.Split('.')[0]] = smr;
        if (origCh17Body == null || !source.ContainsKey("Ch17_Body"))
        {
            Debug.LogError("[BaseBodyBuilder] Ch17_Body missing from " + PlayerAnimationBuilder.ModelPath + " or " + SourceFbx);
            return;
        }
        // Blender's copy of Ch17_Body is the same mesh in Blender's FBX space: its bounds give the space change.
        var newCh17 = source["Ch17_Body"].sharedMesh.bounds; var oldCh17 = origCh17Body.sharedMesh.bounds;
        float scale = oldCh17.size.magnitude / newCh17.size.magnitude;
        var toOriginal = Matrix4x4.TRS(oldCh17.center - newCh17.center * scale, Quaternion.identity, Vector3.one * scale);
        Debug.Log($"[BaseBodyBuilder] Blender FBX -> Ch17 space: scale {scale:F5}, size check " +
                  $"{(newCh17.size * scale - oldCh17.size).magnitude * 1000f:F2} mm");

        var bones = new Dictionary<string, Transform>();
        foreach (var t in body.GetComponentsInChildren<Transform>(true)) bones[t.name] = t;
        var template = body.Find("Ch17_Body").GetComponent<SkinnedMeshRenderer>();

        var original = AssetDatabase.LoadAssetAtPath<Material>(Ch17Material);
        var materials = new Dictionary<string, Material>
        {
            ["Base_Body"] = SaveMaterial("Base_Skin", original.shader, SkinTexture, original.GetFloat("_Smoothness")),
            ["Base_Underwear"] = SaveMaterial("Base_Underwear", original.shader, ClothTexture, 0.1f),
        };
        template.sharedMaterial = PlayerHeadMaterialAsset(original);

        foreach (var part in Parts)
        {
            if (!source.TryGetValue(part, out var src)) { Debug.LogError($"[BaseBodyBuilder] {part} missing from {SourceFbx}"); return; }
            // Into Blender's Ch17_Body space (through any bone both meshes share), then into the original's.
            var srcCh17 = source["Ch17_Body"];
            int hips = System.Array.FindIndex(src.bones, b => b.name == "mixamorig1:Hips");
            int ch17Hips = System.Array.FindIndex(srcCh17.bones, b => b.name == "mixamorig1:Hips");
            var toCh17 = srcCh17.sharedMesh.bindposes[ch17Hips].inverse * src.sharedMesh.bindposes[hips];
            var mesh = Convert(src, toOriginal * toCh17, bindposes, out var boneNames);
            if (mesh == null) return;
            mesh = SaveMesh(mesh, part);

            var go = body.Find(part)?.gameObject;
            if (go == null)
            {
                go = new GameObject(part);
                Undo.RegisterCreatedObjectUndo(go, "Apply Base Body");
                go.transform.SetParent(body, false);
            }
            go.layer = template.gameObject.layer;
            go.SetActive(true);
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null) smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = boneNames.ConvertAll(n => bones[n]).ToArray();
            smr.rootBone = bones["mixamorig1:Hips"];
            smr.localBounds = new Bounds(Vector3.zero, new Vector3(2f, 2.2f, 2f));  // around the hips; room for any pose
            smr.sharedMaterial = materials[part];
            smr.shadowCastingMode = template.shadowCastingMode;
            smr.receiveShadows = template.receiveShadows;
            smr.updateWhenOffscreen = false;
            Debug.Log($"[BaseBodyBuilder] {part}: {mesh.vertexCount} verts, {boneNames.Count} bones");
        }

        SetOutfit(body, false);
        Save();
    }

    // Headless: Unity.exe -batchmode -quit -projectPath ... -executeMethod BaseBodyBuilder.ApplyFromCommandLine
    public static void ApplyFromCommandLine()
    {
        EditorSceneManager.OpenScene("Assets/ProjectZed.unity");
        Apply();
    }

    const string MannequinName = "BaseBody_Mannequin";
    const string MannequinIdle = "Assets/Characters/Animations/Imported/Locomotion Pack/idle.fbx";  // relaxed, arms down
    const string MannequinControllerPath = OutDir + "/Mannequin.controller";

    // Just the relaxed idle (the player's controller would hold an imaginary rifle).
    static RuntimeAnimatorController MannequinController()
    {
        var existing = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(MannequinControllerPath);
        if (existing) return existing;
        AnimationClip clip = null;
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(MannequinIdle))
            if (o is AnimationClip c && !c.name.StartsWith("__preview")) clip = c;
        return UnityEditor.Animations.AnimatorController.CreateAnimatorControllerAtPathWithClip(MannequinControllerPath, clip);
    }

    // Tools > Characters > Create Base Body Mannequin: a Ch17 that only stands and idles, wearing the base body, a few
    // metres in front of the player and facing him, to look at up close. Re-running replaces it.
    [MenuItem("Tools/Characters/Create Base Body Mannequin")]
    public static void CreateMannequin()
    {
        var player = PlayerBody();
        if (player == null) return;
        if (player.Find("Base_Body") == null) Apply();
        var old = GameObject.Find(MannequinName);
        if (old) Undo.DestroyObjectImmediate(old);

        var root = new GameObject(MannequinName);
        Undo.RegisterCreatedObjectUndo(root, "Create Base Body Mannequin");
        var playerRoot = player.parent;
        var pos = playerRoot.position + playerRoot.forward * 3f;
        // Stand on whatever is below (terrain or floor), ignoring the player himself.
        foreach (var hit in Physics.RaycastAll(pos + Vector3.up * 5f, Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore))
            if (!hit.transform.IsChildOf(playerRoot) && hit.point.y > pos.y - 5f) { pos.y = hit.point.y; break; }
        root.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-playerRoot.forward));

        var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PlayerAnimationBuilder.ModelPath), root.transform);
        model.name = "Body";
        var source = player.GetComponent<Animator>();
        var animator = model.GetComponent<Animator>();
        if (animator == null) animator = model.AddComponent<Animator>();
        animator.avatar = source.avatar;
        animator.runtimeAnimatorController = MannequinController();
        animator.applyRootMotion = false;

        // Same meshes and materials as the player's, bound to the mannequin's own bones.
        var bones = new Dictionary<string, Transform>();
        foreach (var t in model.GetComponentsInChildren<Transform>(true)) bones[t.name] = t;
        foreach (var part in Parts)
        {
            var from = player.Find(part).GetComponent<SkinnedMeshRenderer>();
            var go = new GameObject(part) { layer = from.gameObject.layer };
            go.transform.SetParent(model.transform, false);
            var smr = go.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = from.sharedMesh;
            smr.bones = System.Array.ConvertAll(from.bones, b => bones[b.name]);
            smr.rootBone = bones[from.rootBone.name];
            smr.localBounds = from.localBounds;
            smr.sharedMaterials = from.sharedMaterials;
            smr.shadowCastingMode = from.shadowCastingMode;
            smr.receiveShadows = from.receiveShadows;
        }
        model.transform.Find("Ch17_Body").GetComponent<SkinnedMeshRenderer>().sharedMaterial =
            player.Find("Ch17_Body").GetComponent<SkinnedMeshRenderer>().sharedMaterial;
        SetOutfit(model.transform, false);
        Selection.activeGameObject = root;
        Save();
        Debug.Log($"[BaseBodyBuilder] {MannequinName} placed at {pos}");
    }

    [MenuItem("Tools/Characters/Restore Player Outfit")]
    public static void Restore()
    {
        var body = PlayerBody();
        if (body == null) return;
        foreach (var part in Parts) body.Find(part)?.gameObject.SetActive(false);
        var head = body.Find("Ch17_Body").GetComponent<SkinnedMeshRenderer>();
        Undo.RecordObject(head, "Restore Player Outfit");
        head.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(Ch17Material);
        SetOutfit(body, true);
        Save();
    }

    static Transform PlayerBody()
    {
        var player = GameObject.Find("Player");
        var driver = player ? player.GetComponentInChildren<PlayerCharacterAnimator>(true) : null;
        if (driver == null) Debug.LogError("[BaseBodyBuilder] No Player with a PlayerCharacterAnimator in the open scene");
        else if (driver.transform.Find("Ch17_Body") == null)
        {
            Debug.LogError("[BaseBodyBuilder] The player no longer uses the Ch17 model (see Tools > Characters > Apply Base Male to All Characters)");
            return null;
        }
        return driver ? driver.transform : null;
    }

    static IEnumerable<SkinnedMeshRenderer> Renderers(string fbx)
    {
        var root = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
        return root ? root.GetComponentsInChildren<SkinnedMeshRenderer>(true) : new SkinnedMeshRenderer[0];
    }

    static bool Close(Matrix4x4 x, Matrix4x4 y)
    {
        for (int i = 0; i < 16; i++) if (Mathf.Abs(x[i] - y[i]) > 1e-3f) return false;
        return true;
    }

    // A copy of src's mesh moved by `space`, skinned to the bones it uses with the original bind poses.
    static Mesh Convert(SkinnedMeshRenderer src, Matrix4x4 space, Dictionary<string, Matrix4x4> bindposes, out List<string> boneNames)
    {
        var mesh = Object.Instantiate(src.sharedMesh);
        var verts = mesh.vertices; var normals = mesh.normals; var tangents = mesh.tangents;
        for (int i = 0; i < verts.Length; i++)
        {
            verts[i] = space.MultiplyPoint3x4(verts[i]);
            if (i < normals.Length) normals[i] = space.MultiplyVector(normals[i]).normalized;
            if (i < tangents.Length)
            {
                var t = space.MultiplyVector(tangents[i]).normalized;
                tangents[i] = new Vector4(t.x, t.y, t.z, tangents[i].w);
            }
        }
        mesh.vertices = verts; mesh.normals = normals; mesh.tangents = tangents;

        // Only the bones the weights use, renumbered, each with the player skeleton's bind pose.
        boneNames = new List<string>();
        var remap = new Dictionary<int, int>();
        var perVertex = mesh.GetBonesPerVertex();
        var weights = new NativeArray<BoneWeight1>(mesh.GetAllBoneWeights(), Allocator.Temp);
        for (int i = 0; i < weights.Length; i++)
        {
            var w = weights[i];
            if (!remap.TryGetValue(w.boneIndex, out int idx))
            {
                var name = src.bones[w.boneIndex].name;
                if (!bindposes.ContainsKey(name))
                {
                    Debug.LogError($"[BaseBodyBuilder] {src.name}: no bind pose for {name} in the original Ch17 FBX");
                    weights.Dispose();
                    return null;
                }
                idx = remap[w.boneIndex] = boneNames.Count;
                boneNames.Add(name);
            }
            w.boneIndex = idx; weights[i] = w;
        }
        mesh.SetBoneWeights(new NativeArray<byte>(perVertex, Allocator.Temp), weights);
        weights.Dispose();
        mesh.bindposes = boneNames.ConvertAll(n => bindposes[n]).ToArray();
        mesh.RecalculateBounds();
        return mesh;
    }

    static Mesh SaveMesh(Mesh mesh, string name)
    {
        mesh.name = name;
        var path = $"{OutDir}/{name}.asset";
        // Replace rather than copy into the old asset: copying left the editor drawing nothing for the new mesh.
        // Only this tool's renderers use these meshes, and it reassigns them straight after.
        if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != null) AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }

    static Material SaveMaterial(string name, Shader shader, string texture, float smoothness)
    {
        var path = $"{OutDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
        mat.shader = shader;
        mat.SetColor("_BaseColor", Color.white);
        mat.SetTexture("_BaseMap", LoadTexture(texture, 2048));
        mat.SetFloat("_Smoothness", smoothness);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    // Ch17's material with the player's copy of its colour map; everything else (normal map, smoothness) unchanged.
    static Material PlayerHeadMaterialAsset(Material original)
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(PlayerHeadMaterial);
        if (mat == null) { mat = new Material(original); AssetDatabase.CreateAsset(mat, PlayerHeadMaterial); }
        else mat.CopyPropertiesFromMaterial(original);
        var sourceSize = ((TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(original.GetTexture("_BaseMap")))).maxTextureSize;
        var tex = LoadTexture(PlayerHeadTexture, sourceSize);
        mat.SetTexture("_BaseMap", tex); mat.SetTexture("_MainTex", tex);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Texture2D LoadTexture(string path, int maxSize)
    {
        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        if (importer.maxTextureSize != maxSize) { importer.maxTextureSize = maxSize; importer.SaveAndReimport(); }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    static void SetOutfit(Transform body, bool on)
    {
        foreach (var name in Outfit)
        {
            var t = body.Find(name);
            if (t) { Undo.RecordObject(t.gameObject, "Apply Base Body"); t.gameObject.SetActive(on); }
        }
    }

    static void Save()
    {
        AssetDatabase.SaveAssets();
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
    }
}
