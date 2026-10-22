using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Characters > Set Up Base Male: imports the naked base male built in Blender
// (Blender/Scripts/make_human.py, MPFB/MakeHuman, CC0 assets) as a Humanoid with its own avatar, gives it URP
// materials, and stands it in front of the player as BaseMale_Mannequin (relaxed idle) to look at. Re-run after
// re-exporting from Blender; it replaces the mannequin (and the older BaseBody_Mannequin).
public static class BaseMaleSetup
{
    const string Dir = "Assets/Characters/BaseMale";
    const string Fbx = Dir + "/BaseMale.fbx";
    const string Tex = Dir + "/Textures/";
    const string MatDir = Dir + "/Materials";
    const string IdleController = "Assets/Characters/BaseBody/Mannequin.controller";
    const string MannequinName = "BaseMale_Mannequin";

    // FBX material name (from MPFB) -> texture, smoothness, alpha-cut and two-sided (eyebrows, eyelashes).
    static readonly (string fbxName, string texture, float smoothness, bool cutout)[] Materials =
    {
        ("BaseMale_Body.body", "young_lightskinned_male_diffuse.png", 0.35f, false),
        ("BaseMale_Body.high-poly", "brown_eye.png", 0.85f, false),  // drawn see-through, see the eyes note below
        ("BaseMale_Body.eyebrow001", "eyebrow001.png", 0.1f, true),
        ("BaseMale_Body.eyelashes01", "eyelashes01.png", 0.1f, true),
        ("BaseMale_Body.teeth_base", "teeth.png", 0.6f, false),
        ("BaseMale_Body.tongue01", "tongue01_diffuse.png", 0.5f, false),
    };

    [MenuItem("Tools/Characters/Set Up Base Male")]
    public static void SetUp()
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(Fbx);
        if (importer == null) { Debug.LogError("[BaseMaleSetup] Missing " + Fbx); return; }
        importer.animationType = ModelImporterAnimationType.Human;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        importer.importAnimation = false;
        importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (!AssetDatabase.IsValidFolder(MatDir)) AssetDatabase.CreateFolder(Dir, "Materials");
        foreach (var (fbxName, texture, smoothness, cutout) in Materials)
            importer.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), fbxName),
                              MakeMaterial(fbxName.Replace("BaseMale_Body.", "BaseMale_"), shader, texture, smoothness, cutout));
        importer.SaveAndReimport();

        var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(Fbx);
        Debug.Log($"[BaseMaleSetup] avatar {(avatar ? avatar.name : "none")}: valid={avatar && avatar.isValid} human={avatar && avatar.isHuman}");
        if (avatar == null || !avatar.isHuman) { Debug.LogError("[BaseMaleSetup] Humanoid avatar was not created"); return; }
        PlaceMannequin();
    }

    // Tools > Characters > Apply Base Male to All Characters: the player and every NPC (anything driven by
    // PlayerCharacterAnimator) get the base male's meshes and skeleton in place of Ch17's, keep their own components
    // and WorldGun, switch to his avatar, and get their ragdolls rebuilt on the new bones. Everything finds bones via
    // the humanoid avatar, so animations, aiming, hit zones and the first-person head hiding carry over.
    [MenuItem("Tools/Characters/Apply Base Male to All Characters")]
    public static void ApplyToAll()
    {
        var avatar = AssetDatabase.LoadAssetAtPath<Avatar>(Fbx);
        var model = AssetDatabase.LoadAssetAtPath<GameObject>(Fbx);
        if (avatar == null || !avatar.isHuman) { Debug.LogError("[BaseMaleSetup] Run Tools > Characters > Set Up Base Male first"); return; }
        int swapped = 0;
        foreach (var driver in Object.FindObjectsByType<PlayerCharacterAnimator>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            var body = driver.transform;
            int layer = body.gameObject.layer;
            var oldMesh = body.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (oldMesh) layer = oldMesh.gameObject.layer;
            // Old meshes and skeleton out (the gun and any other attachments stay).
            foreach (Transform child in body.Cast<Transform>().ToArray())
                if (child.GetComponent<SkinnedMeshRenderer>() || child.name.StartsWith("mixamorig") || child.name.StartsWith("Base_"))
                    Undo.DestroyObjectImmediate(child.gameObject);
            // New ones in, at the same paths the avatar expects (relative to the model's root).
            var instance = (GameObject)Object.Instantiate(model);
            foreach (Transform child in instance.transform.Cast<Transform>().ToArray())
            {
                child.SetParent(body, false);
                Undo.RegisterCreatedObjectUndo(child.gameObject, "Apply Base Male");
                foreach (var r in child.GetComponentsInChildren<Renderer>(true)) r.gameObject.layer = layer;
            }
            Object.DestroyImmediate(instance);
            var animator = body.GetComponent<Animator>();
            Undo.RecordObject(animator, "Apply Base Male");
            animator.avatar = avatar;
            animator.Rebind();
            swapped++;
            Debug.Log($"[BaseMaleSetup] {body.parent.name}: now the base male");
        }
        RagdollBuilder.Build();  // rebuilds ragdolls on the new bones and saves the scene
        Debug.Log($"[BaseMaleSetup] Swapped {swapped} characters");
    }

    static Material MakeMaterial(string name, Shader shader, string texture, float smoothness, bool cutout)
    {
        var path = $"{MatDir}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) { mat = new Material(shader); AssetDatabase.CreateAsset(mat, path); }
        mat.shader = shader;
        if (cutout)
        {
            // Fine hair strands: keep them through the mip levels (otherwise they thin away to a line at a distance),
            // and stop the transparent border bleeding dark colour into them.
            var ti = (TextureImporter)AssetImporter.GetAtPath(Tex + texture);
            if (!ti.alphaIsTransparency || !ti.mipMapsPreserveCoverage)
            {
                ti.alphaIsTransparency = true; ti.mipMapsPreserveCoverage = true; ti.alphaTestReferenceValue = 0.3f;
                ti.SaveAndReimport();
            }
        }
        mat.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(Tex + texture));
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Smoothness", smoothness);
        mat.SetFloat("_AlphaClip", cutout ? 1 : 0);
        mat.SetFloat("_Cutoff", 0.3f);
        mat.SetFloat("_Cull", cutout ? 0 : 2);  // eyebrow and eyelash cards are seen from both sides
        if (cutout) { mat.EnableKeyword("_ALPHATEST_ON"); mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest; }
        else { mat.DisableKeyword("_ALPHATEST_ON"); mat.renderQueue = -1; }
        // Eyes: each eye is an eyeball plus a clear outer shell (the cornea), mapped onto the pale-blue disc in the
        // texture's corner. Drawn opaque, that shell hides the iris; it needs to be transparent and drawn last.
        if (name.EndsWith("high-poly"))
        {
            mat.SetFloat("_Surface", 1); mat.SetFloat("_Blend", 0);
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static void PlaceMannequin()
    {
        var player = GameObject.Find("Player");
        if (player == null) { Debug.LogError("[BaseMaleSetup] No Player in the open scene"); return; }
        foreach (var name in new[] { MannequinName, "BaseBody_Mannequin" })
        {
            var old = GameObject.Find(name);
            if (old) Undo.DestroyObjectImmediate(old);
        }
        var p = player.transform;
        var pos = p.position + p.forward * 3f;
        foreach (var hit in Physics.RaycastAll(pos + Vector3.up * 5f, Vector3.down, 20f, ~0, QueryTriggerInteraction.Ignore))
            if (!hit.transform.IsChildOf(p) && hit.point.y > pos.y - 5f) { pos.y = hit.point.y; break; }
        var root = new GameObject(MannequinName);
        Undo.RegisterCreatedObjectUndo(root, "Set Up Base Male");
        root.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(-p.forward));
        var model = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Fbx), root.transform);
        model.name = "Body";
        var animator = model.GetComponent<Animator>();
        if (animator == null) animator = model.AddComponent<Animator>();
        animator.avatar = AssetDatabase.LoadAssetAtPath<Avatar>(Fbx);
        animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(IdleController);
        animator.applyRootMotion = false;
        Selection.activeGameObject = root;
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[BaseMaleSetup] {MannequinName} placed at {pos}");
    }
}
