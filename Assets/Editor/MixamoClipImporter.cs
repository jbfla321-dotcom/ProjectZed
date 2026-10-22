using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Sets up Mixamo animation FBX files dropped under Characters/Animations/Imported on their first import:
// humanoid on the character's own avatar, no materials/cameras/lights, root rotation and height
// baked into the pose (the character controller moves and turns the player), loops for cyclic moves.
// Only runs when the file has no import settings yet, so later manual tweaks are kept.
public class MixamoClipImporter : AssetPostprocessor
{
    const string Folder = "Assets/Characters/Animations/Imported/";

    // Clips whose names contain one of these play once instead of looping.
    static readonly string[] OneShot = { "death", "jump up", "jump down", "turn", "reload", "hit", "toss", "kneel", "firing" };

    bool Applies => assetPath.StartsWith(Folder) && assetImporter.importSettingsMissing;

    // Mixamo's animation-only downloads store the clip's first frame as the skeleton's rest pose, not a T-pose,
    // so an avatar created from the clip file bakes that pose's offsets into every frame (e.g. a foot that
    // stays tilted toes-up through the walk). The clips share the character's skeleton (same bone names), so
    // they use the character's avatar instead.
    static Avatar CharacterAvatar() =>
        AssetDatabase.LoadAllAssetsAtPath(PlayerAnimationBuilder.ModelPath).OfType<Avatar>().FirstOrDefault();

    void OnPreprocessModel()
    {
        if (!Applies) return;
        var mi = (ModelImporter)assetImporter;
        mi.animationType = ModelImporterAnimationType.Human;
        var avatar = CharacterAvatar();
        if (avatar != null)
        {
            context.DependsOnSourceAsset(PlayerAnimationBuilder.ModelPath);
            mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            mi.sourceAvatar = avatar;
        }
        else mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        mi.materialImportMode = ModelImporterMaterialImportMode.None;
        mi.importCameras = false;
        mi.importLights = false;
        mi.importBlendShapes = false;
        mi.resampleCurves = true;
        mi.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
    }

    void OnPreprocessAnimation()
    {
        if (!Applies) return;
        var mi = (ModelImporter)assetImporter;
        string name = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
        bool loop = System.Array.TrueForAll(OneShot, w => !name.Contains(w));
        var clips = mi.defaultClipAnimations;
        foreach (var c in clips)
        {
            c.name = Path.GetFileNameWithoutExtension(assetPath);
            c.loopTime = loop;
            c.loopPose = loop;
            c.lockRootRotation = true;
            c.keepOriginalOrientation = true;
            c.lockRootHeightY = true;
            c.keepOriginalPositionY = true;
            c.lockRootPositionXZ = false;
        }
        mi.clipAnimations = clips;
    }

    // Moves clips imported before the avatar fix onto the character's avatar (their clip settings are kept).
    [MenuItem("Tools/Characters/Retarget Imported Clips To Character")]
    public static void RetargetImportedClips()
    {
        var avatar = CharacterAvatar();
        if (avatar == null) { Debug.LogError("[MixamoClipImporter] No avatar on " + PlayerAnimationBuilder.ModelPath); return; }
        int changed = 0;
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var path in Directory.GetFiles(Folder, "*.fbx", SearchOption.AllDirectories))
            {
                var mi = (ModelImporter)AssetImporter.GetAtPath(path.Replace('\\', '/'));
                if (mi == null || (mi.avatarSetup == ModelImporterAvatarSetup.CopyFromOther && mi.sourceAvatar == avatar)) continue;
                mi.animationType = ModelImporterAnimationType.Human;
                mi.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
                mi.sourceAvatar = avatar;
                mi.SaveAndReimport();
                changed++;
            }
        }
        finally { AssetDatabase.StopAssetEditing(); }
        Debug.Log($"[MixamoClipImporter] Retargeted {changed} clips onto {avatar.name}");
    }
}
