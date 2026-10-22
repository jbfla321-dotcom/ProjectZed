using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Characters > Build Ragdolls: gives every character body in the scene (anything driven by
// PlayerCharacterAnimator) a ragdoll on its 11 main bones (Rigidbody + collider + CharacterJoint), the
// same set Unity's Ragdoll Wizard makes, sized from the skeleton. Also sets up the Character/Hitbox
// layers and adds CharacterHealth to each character. Safe to re-run: old ragdoll parts are replaced.
public static class RagdollBuilder
{
    const float TotalMass = 75f;

    [MenuItem("Tools/Characters/Build Ragdolls")]
    public static void Build()
    {
        if (!EnsureLayers()) return;
        int built = 0;
        foreach (var driver in Object.FindObjectsByType<PlayerCharacterAnimator>(FindObjectsInactive.Include))
        {
            BuildRagdoll(driver);
            var root = driver.transform.parent;
            root.gameObject.layer = CharacterLayers.Character;
            if (root.GetComponent<CharacterHealth>() == null) root.gameObject.AddComponent<CharacterHealth>();
            built++;
        }
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        Debug.Log($"[RagdollBuilder] Built {built} ragdolls");
    }

    static bool EnsureLayers()
    {
        var tags = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
        var layers = tags.FindProperty("layers");
        foreach (var name in new[] { "Character", "Hitbox" })
        {
            if (LayerMask.NameToLayer(name) >= 0) continue;
            bool placed = false;
            for (int i = 8; i < 32 && !placed; i++)
            {
                var p = layers.GetArrayElementAtIndex(i);
                if (string.IsNullOrEmpty(p.stringValue)) { p.stringValue = name; placed = true; }
            }
            if (!placed) { Debug.LogError("[RagdollBuilder] No free layer for " + name); return false; }
        }
        tags.ApplyModifiedPropertiesWithoutUndo();
        int character = LayerMask.NameToLayer("Character"), hitbox = LayerMask.NameToLayer("Hitbox");
        // A body's limbs never collide with movement capsules (its own or anyone's).
        Physics.IgnoreLayerCollision(character, hitbox, true);
        AssetDatabase.SaveAssets();
        return true;
    }

    static void BuildRagdoll(PlayerCharacterAnimator driver)
    {
        var animator = driver.GetComponent<Animator>();
        Transform body = driver.transform;
        ResetToBindPose(animator);
        RemoveOld(animator);

        Transform B(HumanBodyBones b) => animator.GetBoneTransform(b);
        Transform hips = B(HumanBodyBones.Hips), chest = B(HumanBodyBones.Chest) ?? B(HumanBodyBones.Spine), head = B(HumanBodyBones.Head);
        Transform neck = B(HumanBodyBones.Neck) ?? head;
        // Proportions from the skeleton: everything scales with the hips-to-head distance.
        float size = Vector3.Distance(hips.position, head.position) / 0.68f;
        Vector3 right = body.right, up = body.up, forward = body.forward;

        // Pelvis and chest are boxes; limbs are capsules from each bone to the next; the head is a sphere.
        var hipsBody = Part(hips, 0.16f);
        float legSpan = Vector3.Distance(B(HumanBodyBones.LeftUpperLeg).position, B(HumanBodyBones.RightUpperLeg).position);
        Box(hips, (hips.position + chest.position) * 0.5f + up * -0.02f * size,
            new Vector3(legSpan * 1.9f, Vector3.Distance(hips.position, chest.position) + 0.12f * size, 0.22f * size), right, up, forward);

        var chestBody = Part(chest, 0.22f);
        float shoulderSpan = Vector3.Distance(B(HumanBodyBones.LeftUpperArm).position, B(HumanBodyBones.RightUpperArm).position);
        Box(chest, (chest.position + neck.position) * 0.5f,
            new Vector3(shoulderSpan * 1.05f, Vector3.Distance(chest.position, neck.position) + 0.06f * size, 0.24f * size), right, up, forward);
        Joint(chest, chestBody, hipsBody, right, forward, -20f, 30f, 15f);

        var headBody = Part(head, 0.08f);
        var sphere = head.gameObject.AddComponent<SphereCollider>();
        sphere.center = head.InverseTransformPoint(head.position + up * 0.09f * size);
        sphere.radius = 0.11f * size / head.lossyScale.x;
        Joint(head, headBody, chestBody, right, forward, -40f, 40f, 30f);

        foreach (bool left in new[] { true, false })
        {
            float side = left ? 1f : -1f;
            Transform upperArm = B(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
            Transform lowerArm = B(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
            Transform hand = B(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
            Transform upperLeg = B(left ? HumanBodyBones.LeftUpperLeg : HumanBodyBones.RightUpperLeg);
            Transform lowerLeg = B(left ? HumanBodyBones.LeftLowerLeg : HumanBodyBones.RightLowerLeg);
            Transform foot = B(left ? HumanBodyBones.LeftFoot : HumanBodyBones.RightFoot);

            // Arms (bind pose: straight out to the side). Positive twist lowers the arm / bends the elbow
            // forward on both sides, so the right side's axes are mirrored.
            var upperArmBody = Part(upperArm, 0.03f);
            Capsule(upperArm, lowerArm.position, 0.2f);
            Joint(upperArm, upperArmBody, chestBody, forward * side, up, -30f, 100f, 70f);
            var lowerArmBody = Part(lowerArm, 0.02f);
            Capsule(lowerArm, hand.position + (hand.position - lowerArm.position).normalized * 0.08f * size, 0.17f);
            Joint(lowerArm, lowerArmBody, upperArmBody, up * side, forward, -5f, 130f, 0f);

            // Legs: thighs swing forward (negative) more than back; knees only bend backward.
            var upperLegBody = Part(upperLeg, 0.10f);
            Capsule(upperLeg, lowerLeg.position, 0.17f);
            Joint(upperLeg, upperLegBody, hipsBody, right, forward, -100f, 30f, 30f);
            var lowerLegBody = Part(lowerLeg, 0.05f);
            Capsule(lowerLeg, foot.position, 0.14f);
            Joint(lowerLeg, lowerLegBody, upperLegBody, right, forward, -5f, 130f, 0f);
        }

        if (body.GetComponent<Ragdoll>() == null) body.gameObject.AddComponent<Ragdoll>();
        EditorUtility.SetDirty(body.gameObject);
    }

    // Joints remember the pose they're created in as "rest", so build from the model's own bind (T) pose.
    static void ResetToBindPose(Animator animator)
    {
        var model = PrefabUtility.GetCorrespondingObjectFromOriginalSource(animator.gameObject);
        if (model == null) return;
        var source = new Dictionary<string, Transform>();
        foreach (var t in model.GetComponentsInChildren<Transform>(true)) source[t.name] = t;
        foreach (HumanBodyBones b in System.Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (b == HumanBodyBones.LastBone) continue;
            var bone = animator.GetBoneTransform(b);
            if (bone != null && source.TryGetValue(bone.name, out var s))
            {
                bone.localPosition = s.localPosition;
                bone.localRotation = s.localRotation;
            }
        }
    }

    static void RemoveOld(Animator animator)
    {
        Transform gun = animator.GetComponent<PlayerCharacterAnimator>().carriedGun;
        foreach (var t in animator.GetBoneTransform(HumanBodyBones.Hips).GetComponentsInChildren<Transform>(true))
        {
            if (gun != null && t.IsChildOf(gun)) continue;
            foreach (var j in t.GetComponents<CharacterJoint>()) Object.DestroyImmediate(j);
            foreach (var c in t.GetComponents<Collider>()) Object.DestroyImmediate(c);
            foreach (var rb in t.GetComponents<Rigidbody>()) Object.DestroyImmediate(rb);
        }
    }

    static Rigidbody Part(Transform bone, float massShare)
    {
        bone.gameObject.layer = CharacterLayers.Hitbox;
        var rb = bone.gameObject.AddComponent<Rigidbody>();
        rb.mass = TotalMass * massShare;
        rb.isKinematic = true; // follows the animation until the body goes limp
        rb.linearDamping = 0.05f;
        rb.angularDamping = 0.5f;
        rb.solverIterations = 12;
        return rb;
    }

    static void Box(Transform bone, Vector3 worldCenter, Vector3 worldSize, Vector3 right, Vector3 up, Vector3 forward)
    {
        var box = bone.gameObject.AddComponent<BoxCollider>();
        box.center = bone.InverseTransformPoint(worldCenter);
        // Size along the body's axes, expressed in the bone's (possibly rotated, scaled) space.
        Vector3 x = bone.InverseTransformVector(right * worldSize.x), y = bone.InverseTransformVector(up * worldSize.y), z = bone.InverseTransformVector(forward * worldSize.z);
        box.size = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), Mathf.Abs(x.y) + Mathf.Abs(y.y) + Mathf.Abs(z.y), Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z));
    }

    // A capsule from the bone to `end`, radius as a fraction of its length.
    static void Capsule(Transform bone, Vector3 end, float radiusOfLength)
    {
        Vector3 local = bone.InverseTransformPoint(end);
        int axis = Mathf.Abs(local.x) > Mathf.Abs(local.y) ? (Mathf.Abs(local.x) > Mathf.Abs(local.z) ? 0 : 2) : (Mathf.Abs(local.y) > Mathf.Abs(local.z) ? 1 : 2);
        float length = Mathf.Abs(local[axis]);
        var capsule = bone.gameObject.AddComponent<CapsuleCollider>();
        capsule.direction = axis;
        capsule.center = local * 0.5f;
        capsule.radius = length * radiusOfLength;
        capsule.height = length + capsule.radius;
    }

    static void Joint(Transform bone, Rigidbody self, Rigidbody parent, Vector3 worldTwistAxis, Vector3 worldSwingAxis,
        float lowTwist, float highTwist, float swing)
    {
        var joint = bone.gameObject.AddComponent<CharacterJoint>();
        joint.connectedBody = parent;
        joint.axis = bone.InverseTransformDirection(worldTwistAxis).normalized;
        joint.swingAxis = bone.InverseTransformDirection(worldSwingAxis).normalized;
        joint.lowTwistLimit = new SoftJointLimit { limit = lowTwist };
        joint.highTwistLimit = new SoftJointLimit { limit = highTwist };
        joint.swing1Limit = new SoftJointLimit { limit = swing };
        joint.swing2Limit = new SoftJointLimit { limit = swing };
        joint.enableProjection = true; // keeps limbs from visibly stretching apart
        joint.enablePreprocessing = false;
    }
}
