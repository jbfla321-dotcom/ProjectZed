using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

// Generates the player's humanoid animation clips procedurally (from muscle curves) and the
// Animator Controller that plays them. Re-run from Tools > Player Character > Build Animations
// after tweaking any numbers below. Every frame is auto-grounded so the feet stay on the floor.
public static class PlayerAnimationBuilder
{
    public const string ModelPath = "Assets/Characters/Mixamo/Ch17_nonPBR.fbx";
    public const string OutDir = "Assets/Characters/Animations";
    public const string ControllerPath = OutDir + "/PlayerCharacter.controller";

    const int SamplesPerCycle = 24;
    // Muscle directions on this rig (found by test renders): higher "Upper Leg Front-Back" swings the leg
    // backward, higher "Spine/Chest Front-Back" leans back, higher "Foot Up-Down" points the toes down.
    const float LegFwd = -1f, LeanFwd = -1f, ToesUp = -1f;
    const float WalkSpeed = 5f, SprintSpeed = 8.5f, CrouchSpeed = 2.5f;

    // A pose is a set of muscle overrides; anything not listed uses the rifle stance.
    // The special key BodyPitch tilts the whole body forward (degrees) about the hips.
    const string BodyPitch = "BodyPitch";
    class Pose : Dictionary<string, float>
    {
        public Pose() { }
        public Pose(Pose other) : base(other) { }
        public Pose With(Pose overrides) { var p = new Pose(this); foreach (var kv in overrides) p[kv.Key] = kv.Value; return p; }
    }

    // ---------- Rifle stance shared by every clip ----------
    static Pose Stance()
    {
        var p = new Pose
        {
            ["Spine Front-Back"] = 0.05f * LeanFwd,
            ["Chest Front-Back"] = 0.05f * LeanFwd,
            ["Left Upper Leg Front-Back"] = 0.6f + 0.05f * LegFwd,
            ["Right Upper Leg Front-Back"] = 0.6f - 0.03f * LegFwd,
            ["Left Upper Leg In-Out"] = 0.1f,
            ["Right Upper Leg In-Out"] = 0.1f,
            ["Left Lower Leg Stretch"] = 0.9f,
            ["Right Lower Leg Stretch"] = 0.9f,
            // Arms roughly in a rifle hold; hand IK snaps them onto the gun.
            ["Right Arm Down-Up"] = -0.35f,
            ["Right Arm Front-Back"] = 0.4f,
            ["Right Forearm Stretch"] = -0.1f,
            ["Left Arm Down-Up"] = -0.25f,
            ["Left Arm Front-Back"] = 0.8f,
            ["Left Forearm Stretch"] = 0.2f,
        };
        // Grip: curl fingers around the pistol grip / under the handguard, right index rests on the trigger.
        foreach (var side in new[] { "Left", "Right" })
            foreach (var f in new[] { "Index", "Middle", "Ring", "Little" })
                for (int j = 1; j <= 3; j++)
                    p[$"{side} {f} {j} Stretched"] = -0.7f;
        p["Right Index 1 Stretched"] = 0.2f;
        p["Right Index 2 Stretched"] = -0.1f;
        p["Right Index 3 Stretched"] = 0.1f;
        p["Left Thumb 2 Stretched"] = 0.2f;
        p["Right Thumb 2 Stretched"] = 0.2f;
        return p;
    }

    // Hips tipped forward, thighs brought further forward to keep the shins near vertical.
    static Pose CrouchStance() => Stance().With(new Pose
    {
        [BodyPitch] = 30f,
        ["Spine Front-Back"] = 0.15f * LeanFwd,
        ["Chest Front-Back"] = 0.1f * LeanFwd,
        ["Left Upper Leg Front-Back"] = -1.0f,
        ["Right Upper Leg Front-Back"] = -0.75f,
        ["Left Lower Leg Stretch"] = -0.15f,
        ["Right Lower Leg Stretch"] = 0.0f,
        ["Left Foot Up-Down"] = 0.0f,
        ["Right Foot Up-Down"] = -0.05f,
    });

    // ---------- Clip definitions ----------
    struct Gait
    {
        public float cycle, strideFB, strideSide, kneeLift, footRoll, twist, lean;
    }

    static readonly Gait CrouchWalk = new Gait { cycle = 0.9f, strideFB = 0.25f, strideSide = 0.22f, kneeLift = 0.45f, footRoll = 0.2f, twist = 0.06f, lean = 0f };

    // Directional stepping cycle. dir = (x: right, y: forward) in the character's local space.
    static Pose GaitPose(Pose stance, Gait g, Vector2 dir, float phase)
    {
        float s = Mathf.Sin(phase), c = Mathf.Cos(phase);
        var p = new Pose(stance);
        // Left leg's displacement along the travel direction is +s, right leg's is -s.
        foreach (var (side, d, swing) in new[] { ("Left", s, Mathf.Max(0f, c)), ("Right", -s, Mathf.Max(0f, -c)) })
        {
            // The thigh also lifts forward while the leg swings through.
            p[$"{side} Upper Leg Front-Back"] = stance[$"{side} Upper Leg Front-Back"] + LegFwd * (g.strideFB * dir.y * d + swing * g.kneeLift * 0.35f);
            float lateral = g.strideSide * dir.x * d; // + = toward the character's right
            p[$"{side} Upper Leg In-Out"] = stance[$"{side} Upper Leg In-Out"] + (side == "Left" ? -lateral : lateral);
            p[$"{side} Lower Leg Stretch"] = stance[$"{side} Lower Leg Stretch"] - swing * g.kneeLift;
            // Toes up on the leading leg (heel strike) and while swinging through.
            p[$"{side} Foot Up-Down"] = (stance.TryGetValue($"{side} Foot Up-Down", out var f) ? f : 0f) + ToesUp * (g.footRoll * d * dir.y + swing * 0.2f);
        }
        p["Spine Twist Left-Right"] = g.twist * s * dir.y;
        p["Spine Front-Back"] = stance["Spine Front-Back"] + LeanFwd * g.lean * dir.y;
        p[BodyPitch] = (stance.TryGetValue(BodyPitch, out var pitch) ? pitch : 0f) + g.lean * 60f * dir.y;
        p["Chest Left-Right"] = 0.04f * s;
        return p;
    }

    class ClipDef
    {
        public string name;
        public float length;
        public bool loop = true;
        public Func<float, Pose> pose; // t in [0, 1]
    }

    static List<ClipDef> Clips()
    {
        var stance = Stance();
        var crouch = CrouchStance();
        var list = new List<ClipDef>
        {
            new ClipDef { name = "Idle", length = 2.4f, pose = t => stance.With(new Pose
            {
                ["Chest Front-Back"] = stance["Chest Front-Back"] + 0.03f * Mathf.Sin(t * Mathf.PI * 2),
                ["Spine Left-Right"] = 0.02f * Mathf.Sin(t * Mathf.PI * 2 + 1f),
                ["Head Nod Down-Up"] = 0.03f * Mathf.Sin(t * Mathf.PI * 2 + 0.5f),
            }) },
            // Upper-body actions (played on the masked layer).
            new ClipDef { name = "Fire", length = 0.15f, loop = false, pose = t =>
            {
                float kick = Mathf.Sin(Mathf.Clamp01(t / 0.35f) * Mathf.PI * 0.5f) * (1f - Mathf.Clamp01((t - 0.35f) / 0.65f));
                return stance.With(new Pose
                {
                    // Recoil rocks the chest back.
                    ["Chest Front-Back"] = stance["Chest Front-Back"] - LeanFwd * 0.12f * kick,
                    ["UpperChest Front-Back"] = -LeanFwd * 0.08f * kick,
                    ["Right Shoulder Down-Up"] = 0.15f * kick,
                });
            } },
            new ClipDef { name = "Reload", length = 1.4f, loop = false, pose = t =>
            {
                // Tilt the rifle, drop the left hand to the mag, swap, slap it home.
                float tilt = Mathf.SmoothStep(0, 1, Mathf.Clamp01(t / 0.15f)) * (1f - Mathf.SmoothStep(0, 1, Mathf.Clamp01((t - 0.8f) / 0.2f)));
                float reach = Mathf.Clamp01(Mathf.Sin(Mathf.Clamp01((t - 0.1f) / 0.75f) * Mathf.PI));
                return stance.With(new Pose
                {
                    ["Chest Twist Left-Right"] = 0.12f * tilt,
                    ["Head Nod Down-Up"] = -0.25f * tilt,
                    ["Head Turn Left-Right"] = 0.15f * tilt,
                    ["Left Arm Down-Up"] = Mathf.Lerp(stance["Left Arm Down-Up"], -0.55f, reach),
                    ["Left Arm Front-Back"] = Mathf.Lerp(stance["Left Arm Front-Back"], 0.45f, reach),
                    ["Left Forearm Stretch"] = Mathf.Lerp(stance["Left Forearm Stretch"], -0.55f, reach),
                    ["Left Hand Down-Up"] = -0.3f * reach,
                });
            } },
        };

        var dirs = new (string, Vector2)[] { ("F", Vector2.up), ("B", Vector2.down), ("L", Vector2.left), ("R", Vector2.right) };
        foreach (var (suffix, d) in dirs)
        {
            var dir = d;
            list.Add(new ClipDef { name = "CrouchWalk_" + suffix, length = CrouchWalk.cycle, pose = t => GaitPose(crouch, CrouchWalk, dir, t * Mathf.PI * 2) });
        }
        return list;
    }

    // ---------- Building ----------
    // Which stored clips a controller uses where there's a choice. The live setup is the default; a
    // candidate overrides some slots and is built to its own controller, for side-by-side comparison.
    public class Setup
    {
        public string controllerPath = ControllerPath;
        public AnimationClip idle, walkForward, runForward; // null = the live choice
        // Keep the rifle pack's forward diagonals (false drops them to blend forward and side instead).
        public bool forwardDiagonals;
    }

    // Clips under evaluation (Tools > Player Character > Build Candidate). Promote them into the live
    // choices in BuildController once they've won the side-by-side comparison.
    public const string CandidatePath = OutDir + "/PlayerCharacter_Candidate.controller";
    // Nothing under evaluation: set overrides here to compare them against the live setup.
    static Setup CandidateSetup() => new Setup
    {
        controllerPath = CandidatePath,
    };

    [MenuItem("Tools/Player Character/Build Animations")]
    public static void Build() => Build(new Setup());

    [MenuItem("Tools/Player Character/Build Candidate")]
    public static void BuildCandidate() => Build(CandidateSetup());

    static void Build(Setup setup)
    {
        var clips = BuildClips();
        try { BuildController(clips, setup); }
        finally { measureRig?.Dispose(); measureRig = null; }
        AssetDatabase.SaveAssets();
        Debug.Log($"[PlayerAnimationBuilder] Built {clips.Count} clips and {setup.controllerPath}");
    }

    static Dictionary<string, AnimationClip> BuildClips()
    {
        if (!AssetDatabase.IsValidFolder(OutDir)) AssetDatabase.CreateFolder(Path.GetDirectoryName(OutDir).Replace('\\', '/'), Path.GetFileName(OutDir));

        var rig = Rig.Create();
        var result = new Dictionary<string, AnimationClip>();
        try
        {
            var defs = Clips();
            // Rifle_Aim: the shouldered pose from the first frame of the imported Firing Rifle, held still.
            var firing = ImportedClip(FiringRiflePath);
            var aimPose = firing != null ? rig.SamplePose(firing, 0f) : Stance();
            defs.Add(new ClipDef { name = "Rifle_Aim", length = 1f, pose = t => aimPose });

            // Kneel: resampled from the imported Rifle Stand To Kneel so every frame is grounded on this
            // character; also played backwards to stand up, and its last frame held as the kneeling pose.
            var kneel = ImportedClip(StandToKneelPath);
            if (kneel != null)
            {
                float len = kneel.length;
                defs.Add(new ClipDef { name = "Rifle_KneelDown", length = len, loop = false, pose = t => rig.SamplePose(kneel, t * len) });
                defs.Add(new ClipDef { name = "Rifle_StandUp", length = len, loop = false, pose = t => rig.SamplePose(kneel, (1f - t) * len) });
                var kneelPose = rig.SamplePose(kneel, len);
                defs.Add(new ClipDef { name = "Rifle_Kneel", length = 1f, pose = t => kneelPose });
            }

            foreach (var def in defs)
            {
                int samples = def.loop ? SamplesPerCycle : Mathf.Max(6, Mathf.CeilToInt(def.length * 30f));
                var curves = new AnimationCurve[HumanTrait.MuscleCount];
                for (int m = 0; m < curves.Length; m++) curves[m] = new AnimationCurve();
                var rootY = new AnimationCurve();
                var rootQ = new[] { new AnimationCurve(), new AnimationCurve(), new AnimationCurve(), new AnimationCurve() };

                for (int i = 0; i <= samples; i++)
                {
                    float t01 = i / (float)samples;
                    // Loop clips sample the same pose at the end as at the start.
                    var pose = def.pose(def.loop && i == samples ? 0f : t01);
                    float[] muscles = rig.Muscles(pose);
                    var rot = Quaternion.Euler(pose.TryGetValue(BodyPitch, out var pitch) ? pitch : 0f, 0f, 0f);
                    float bodyY = rig.GroundedBodyY(muscles, rot);
                    float time = t01 * def.length;
                    for (int m = 0; m < muscles.Length; m++) curves[m].AddKey(time, muscles[m]);
                    rootY.AddKey(time, bodyY);
                    for (int q = 0; q < 4; q++) rootQ[q].AddKey(time, rot[q]);
                }

                string path = $"{OutDir}/{def.name}.anim";
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
                if (clip == null) { clip = new AnimationClip(); AssetDatabase.CreateAsset(clip, path); }
                clip.ClearCurves();
                clip.frameRate = 30f;
                for (int m = 0; m < curves.Length; m++)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), ClipMuscleName(HumanTrait.MuscleName[m])), Smooth(curves[m]));
                SetConst(clip, "RootT.x", 0f, def.length);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), "RootT.y"), Smooth(rootY));
                SetConst(clip, "RootT.z", 0f, def.length);
                string[] qNames = { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
                for (int q = 0; q < 4; q++)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), qNames[q]), Smooth(rootQ[q]));

                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                settings.loopTime = def.loop;
                settings.loopBlendOrientation = true;
                settings.loopBlendPositionY = true;
                settings.loopBlendPositionXZ = true;
                settings.keepOriginalOrientation = true;
                settings.keepOriginalPositionY = true;
                settings.keepOriginalPositionXZ = true;
                AnimationUtility.SetAnimationClipSettings(clip, settings);
                EditorUtility.SetDirty(clip);
                result[def.name] = clip;
            }
        }
        finally { rig.Dispose(); }
        return result;
    }

    static AnimationCurve Smooth(AnimationCurve c)
    {
        for (int k = 0; k < c.length; k++) AnimationUtility.SetKeyLeftTangentMode(c, k, AnimationUtility.TangentMode.ClampedAuto);
        for (int k = 0; k < c.length; k++) AnimationUtility.SetKeyRightTangentMode(c, k, AnimationUtility.TangentMode.ClampedAuto);
        return c;
    }

    static void SetConst(AnimationClip clip, string prop, float value, float length) =>
        AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve("", typeof(Animator), prop), AnimationCurve.Constant(0f, length, value));

    // Clip curves name finger muscles "LeftHand.Index.1 Stretched" rather than "Left Index 1 Stretched".
    static string ClipMuscleName(string muscle)
    {
        foreach (var side in new[] { "Left", "Right" })
            foreach (var f in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
            {
                string prefix = $"{side} {f} ";
                if (muscle.StartsWith(prefix)) return $"{side}Hand.{f}.{muscle.Substring(prefix.Length)}";
            }
        return muscle;
    }

    // Temporary posed copy of the character used to convert muscles to bone positions.
    class Rig : IDisposable
    {
        GameObject instance;
        HumanPoseHandler handler;
        Transform leftFoot, rightFoot, hipsBone;
        float[] rest;
        float restFootY, humanScale;

        public static Rig Create()
        {
            var rig = new Rig();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            rig.instance = UnityEngine.Object.Instantiate(prefab);
            rig.instance.hideFlags = HideFlags.HideAndDontSave;
            rig.instance.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            var anim = rig.instance.GetComponent<Animator>();
            rig.humanScale = anim.humanScale;
            rig.handler = new HumanPoseHandler(anim.avatar, rig.instance.transform);
            rig.hipsBone = anim.GetBoneTransform(HumanBodyBones.Hips);
            rig.leftFoot = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
            rig.rightFoot = anim.GetBoneTransform(HumanBodyBones.RightFoot);
            var pose = new HumanPose();
            rig.handler.GetHumanPose(ref pose);
            rig.rest = (float[])pose.muscles.Clone();
            rig.restFootY = Mathf.Min(rig.leftFoot.position.y, rig.rightFoot.position.y);
            return rig;
        }

        // Every muscle of `clip` at `time`, as a pose.
        public Pose SamplePose(AnimationClip clip, float time)
        {
            clip.SampleAnimation(instance, time);
            var hp = new HumanPose();
            handler.GetHumanPose(ref hp);
            var pose = new Pose();
            for (int i = 0; i < hp.muscles.Length; i++) pose[HumanTrait.MuscleName[i]] = hp.muscles[i];
            // Keep the hips' forward tilt (e.g. leaning into a kneel); heading is ignored, height is re-grounded.
            pose[BodyPitch] = Mathf.DeltaAngle(0f, hp.bodyRotation.eulerAngles.x);
            return pose;
        }

        public float[] Muscles(Pose pose)
        {
            var m = (float[])rest.Clone();
            foreach (var kv in pose)
            {
                if (kv.Key == BodyPitch) continue;
                int i = Array.IndexOf(HumanTrait.MuscleName, kv.Key);
                if (i < 0) throw new ArgumentException("Unknown muscle: " + kv.Key);
                m[i] = kv.Value;
            }
            return m;
        }

        // Body height that puts the lower foot on the ground for this pose.
        public float GroundedBodyY(float[] muscles, Quaternion bodyRotation)
        {
            var pose = new HumanPose { bodyPosition = new Vector3(0, 1, 0), bodyRotation = bodyRotation, muscles = muscles };
            handler.SetHumanPose(ref pose);
            float footY = Mathf.Min(leftFoot.position.y, rightFoot.position.y);
            return 1f + (restFootY - footY) / humanScale;
        }

        // Normalized time (0-1) in a locomotion clip when the left foot is furthest ahead along the
        // direction of travel. Used to line up foot timing between blended clips.
        public float LeftFootLeadPhase(AnimationClip clip, Vector3 travel)
        {
            const int samples = 48;
            float best = float.MinValue, bestT = 0f;
            travel.y = 0f;
            travel.Normalize();
            for (int i = 0; i < samples; i++)
            {
                float t = i / (float)samples;
                clip.SampleAnimation(instance, t * clip.length);
                float lead = Vector3.Dot(instance.transform.InverseTransformVector(leftFoot.position - rightFoot.position), travel);
                if (lead > best) { best = lead; bestT = t; }
            }
            return bestT;
        }

        // How fast the planted foot travels backward under the hips (m/s), i.e. the ground speed the legs
        // actually cover. Usually equal to the clip's root speed, but not always (e.g. the rifle pack's
        // sprint forward covers 7.2 m/s of ground while its root moves 6.2).
        public float FootGroundSpeed(AnimationClip clip, Vector3 travel)
        {
            const int samples = 60;
            travel.y = 0f;
            travel.Normalize();
            var left = new Vector3[samples];
            var right = new Vector3[samples];
            float floor = float.MaxValue;
            for (int i = 0; i < samples; i++)
            {
                clip.SampleAnimation(instance, clip.length * i / samples);
                Vector3 hips = hipsBone.position;
                hips.y = 0f;
                left[i] = instance.transform.InverseTransformVector(leftFoot.position - hips);
                right[i] = instance.transform.InverseTransformVector(rightFoot.position - hips);
                floor = Mathf.Min(floor, Mathf.Min(left[i].y, right[i].y));
            }
            // Only frames where the same foot is lowest and on the ground at both ends.
            float sum = 0f, dt = clip.length / samples;
            int contacts = 0;
            for (int i = 0; i < samples; i++)
            {
                int j = (i + 1) % samples;
                bool leftDown = left[i].y < right[i].y;
                Vector3 a = leftDown ? left[i] : right[i], b = leftDown ? left[j] : right[j];
                if ((left[j].y < right[j].y) != leftDown || a.y > floor + 0.03f || b.y > floor + 0.03f) continue;
                sum += -Vector3.Dot(b - a, travel) / dt;
                contacts++;
            }
            return contacts > 0 ? sum / contacts : 0f;
        }

        // Whether a clip's first frame lies on its back (chest facing up) rather than on its front.
        public bool StartsFaceUp(AnimationClip clip)
        {
            clip.SampleAnimation(instance, 0f);
            var hp = new HumanPose();
            handler.GetHumanPose(ref hp);
            return (hp.bodyRotation * Vector3.forward).y > 0f;
        }

        public void Dispose()
        {
            handler?.Dispose();
            if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
        }
    }

    // ---------- Imported clips ----------
    // Mocap clips are used when present; any missing role falls back to the generated clip.
    const string RpgPrefix = "Assets/ExplosiveLLC/RPG Character Mecanim Animation Pack FREE/Animations/Unarmed/RPG-Character@Unarmed-";
    const string ImportedDir = "Assets/Characters/Animations/Imported/";
    const string FiringRiflePath = ImportedDir + "Firing Rifle.fbx";
    const string FiringShotPath = ImportedDir + "Firing Rifle (1).fbx";
    const string RifleIdlePath = ImportedDir + "Rifle Idle.fbx";
    const string RifleRunPath = ImportedDir + "Rifle Run.fbx";
    const string StandToKneelPath = ImportedDir + "Rifle Stand To Kneel.fbx";

    static AnimationClip ImportedClip(string fbxPath, string clipName = null)
    {
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
            if (o is AnimationClip c && !c.name.StartsWith("__preview") && (clipName == null || c.name == clipName)) return c;
        return null;
    }

    static AnimationClip Rpg(string name, string clipName = null) => ImportedClip(RpgPrefix + name + ".FBX", clipName);

    struct Child
    {
        public Motion motion;
        public Vector2 pos;
        public float timeScale;
        public float cycleOffset;
    }

    static Child At(Motion motion, Vector2 pos, float timeScale = 1f) => new Child { motion = motion, pos = pos, timeScale = timeScale };

    // Placed at its own natural velocity, so the feet match the ground at that speed.
    static Child AtNatural(AnimationClip clip) => At(clip, new Vector2(clip.averageSpeed.x, clip.averageSpeed.z));

    // Mixamo "Shooter Pack" clips, by file name.
    const string ShooterDir = ImportedDir + "Shooter Pack/";
    static AnimationClip Shooter(string name) => ImportedClip(ShooterDir + name + ".fbx");

    // Mixamo "Rifle 8-Way Locomotion Pack": walk/run/sprint and crouch-walk in all 8 directions, one consistent
    // rifle-carrying style, plus jump up/loop/down.
    const string RifleLocoDir = ImportedDir + "Rifle Locomotion/";
    static AnimationClip RifleLoco(string name) => ImportedClip(RifleLocoDir + name + ".fbx");
    static readonly string[] EightWay = { "forward", "backward", "left", "right", "forward left", "forward right", "backward left", "backward right" };

    // Mixamo "Action Adventure" pack: the walk/run used straight ahead.
    // Upright like DayZ / Arma (Docs/AnimationReferences.md). Average forward lean, hips to neck, on this
    // character: rifle pack walk 14.1 degrees / run 15.3 (hunched combat moves), Action Adventure walk -2.5 /
    // run 11.5. The rifle pack's side and back moves are upright enough to keep, but its forward diagonals
    // are as hunched as its forward ones, so those are left to blend between forward and side.
    const string ActionDir = ImportedDir + "Action Adventure/";
    static AnimationClip Action(string name) => ImportedClip(ActionDir + name + ".fbx");

    // Reload clip length is matched to the gun's reload time.
    const float ReloadSeconds = 2.2f;

    // Shift each moving clip's cycle so the left foot leads at the same normalized time in all of them.
    // Without this, blending between directions crosses the legs (one clip's left step over another's right).
    static void PhaseMatch(List<Child> children)
    {
        var rig = Rig.Create();
        try
        {
            for (int i = 0; i < children.Count; i++)
            {
                var c = children[i];
                if (!(c.motion is AnimationClip clip) || c.pos.sqrMagnitude < 0.01f) continue;
                var travel = new Vector3(clip.averageSpeed.x, 0f, clip.averageSpeed.z);
                if (travel.sqrMagnitude < 0.01f) travel = new Vector3(c.pos.x, 0f, c.pos.y);
                c.cycleOffset = rig.LeftFootLeadPhase(clip, travel);
                children[i] = c;
            }
        }
        finally { rig.Dispose(); }
    }

    // Above this, legs look like fast-forward; a little foot slide is far less noticeable.
    const float MaxRunTimeScale = 1.35f;

    // Placed at `speed` in the clip's direction and sped up to match (up to a cap), so the feet don't slide.
    // The time scale comes from how fast the clip's planted feet cover ground, which is what the eye judges
    // sliding by (the root speed is used if that can't be measured sensibly).
    static Child AtSpeed(AnimationClip clip, float speed, float maxTimeScale = MaxRunTimeScale)
    {
        var v = new Vector2(clip.averageSpeed.x, clip.averageSpeed.z);
        if (v.magnitude <= 0.01f) return At(clip, Vector2.zero);
        float ground = FootGroundSpeed(clip, new Vector3(v.x, 0f, v.y));
        if (ground < 0.6f * v.magnitude || ground > 1.5f * v.magnitude) ground = v.magnitude;
        return At(clip, v.normalized * speed, Mathf.Min(speed / ground, maxTimeScale));
    }

    // One measuring rig for the whole build (instantiating the character per clip is slow).
    static Rig measureRig;
    static float FootGroundSpeed(AnimationClip clip, Vector3 travel)
    {
        if (measureRig == null) measureRig = Rig.Create();
        return measureRig.FootGroundSpeed(clip, travel);
    }

    // ---------- Animator Controller ----------
    // Base layer:      full-body locomotion (8-way mocap walk/run + sprint), crouch, jump -> fall -> land.
    // UpperBody layer: rifle aim pose over the spine, arms and head (override, masked).
    // Actions layer:   recoil and reload as additive motion on top of the aim (masked).
    // Hand and look-at IK run on top of all of it (PlayerCharacterAnimator).
    static void BuildController(Dictionary<string, AnimationClip> gen, Setup setup)
    {
        // Reuse the existing asset (rather than delete + create) so scene references to it survive a rebuild.
        var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(setup.controllerPath);
        if (ctrl == null)
        {
            ctrl = AnimatorController.CreateAnimatorControllerAtPath(setup.controllerPath);
        }
        else
        {
            while (ctrl.layers.Length > 0) ctrl.RemoveLayer(0);
            ctrl.parameters = new AnimatorControllerParameter[0];
            foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(setup.controllerPath))
                if (sub is BlendTree || sub is AnimatorStateMachine || sub is AnimatorState || sub is AnimatorTransitionBase)
                {
                    AssetDatabase.RemoveObjectFromAsset(sub);
                    UnityEngine.Object.DestroyImmediate(sub, true);
                }
            ctrl.AddLayer("Base Layer");
        }
        ctrl.AddParameter("VelX", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("VelZ", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Crouch", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("Grounded", AnimatorControllerParameterType.Bool);
        ctrl.AddParameter("AirTime", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Raise", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);
        ctrl.AddParameter("Jump", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Fire", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Reload", AnimatorControllerParameterType.Trigger);
        ctrl.AddParameter("Hit", AnimatorControllerParameterType.Trigger);
        var parameters = ctrl.parameters;
        foreach (var p in parameters) if (p.name == "Grounded") p.defaultBool = true;
        ctrl.parameters = parameters;

        // ----- Base layer -----
        var baseLayer = ctrl.layers[0];
        baseLayer.iKPass = true;
        var sm = baseLayer.stateMachine;

        var runF = Rpg("Run-Forward");
        bool mocap = runF != null;
        var rifleIdle = ImportedClip(RifleIdlePath);
        var rifleRun = ImportedClip(RifleRunPath);
        var standing = new List<Child> { At(rifleIdle ?? Rpg("Idle") ?? gen["Idle"], Vector2.zero) };
        // Best source first: the 8-way rifle pack (true diagonals, matched walk/run/sprint), then the 4-way
        // shooter pack, then the RPG clips.
        var walk8 = EightWay.Select(d => RifleLoco("walk " + d)).ToArray();
        var run8 = EightWay.Select(d => RifleLoco("run " + d)).ToArray();
        // Sprinting needs forward input, so only the forward three directions are reachable.
        var sprint3 = new[] { RifleLoco("sprint forward"), RifleLoco("sprint forward left"), RifleLoco("sprint forward right") };
        bool rifle8 = walk8.All(c => c != null) && run8.All(c => c != null) && sprint3.All(c => c != null);
        var shooterWalk = new[] { Shooter("walking"), Shooter("walking backwards"), Shooter("strafe left"), Shooter("strafe right") };
        var shooterRun = new[] { Shooter("rifle run"), Shooter("run backwards"), Shooter("strafe (2)"), Shooter("strafe") };
        mocap |= rifle8;
        if (rifle8)
        {
            // Upright (-2.4 degrees average lean); Action Adventure "idle (3)" leaned 16 (6 to 25).
            var idle = setup.idle ?? RifleLoco("idle");
            if (idle != null) standing[0] = At(idle, Vector2.zero);
            var forwardWalk = setup.walkForward ?? Action("walking");
            var forwardRun = setup.runForward ?? Action("running");
            for (int i = 0; i < EightWay.Length; i++)
            {
                bool ahead = EightWay[i] == "forward", aheadDiagonal = !setup.forwardDiagonals && EightWay[i].StartsWith("forward ");
                if (forwardWalk == null || !(ahead || aheadDiagonal)) standing.Add(AtNatural(walk8[i]));
                else if (ahead) standing.Add(AtNatural(forwardWalk));
                if (forwardRun == null || !(ahead || aheadDiagonal)) standing.Add(AtSpeed(run8[i], WalkSpeed));
                else if (ahead) standing.Add(AtSpeed(forwardRun, WalkSpeed));
            }
            foreach (var sprint in sprint3) standing.Add(AtSpeed(sprint, SprintSpeed));
            PhaseMatch(standing);
        }
        else if (mocap && shooterWalk.All(c => c != null) && shooterRun.All(c => c != null))
        {
            foreach (var walk in shooterWalk) standing.Add(AtNatural(walk));
            foreach (var run in shooterRun) standing.Add(AtSpeed(run, WalkSpeed));
            standing.Add(AtSpeed(runF, SprintSpeed, 1.45f));
            PhaseMatch(standing);
        }
        else if (mocap)
        {
            // Fallback without the shooter pack: 8-way RPG clips, with the rifle run for straight ahead.
            foreach (var d in new[] { "Forward", "Backward", "Left", "Right", "Forward-Left", "Forward-Right", "Backward-Left", "Backward-Right" })
            {
                var walk = Rpg("Strafe-" + d);
                var run = d == "Forward" && rifleRun != null ? rifleRun : Rpg("Run-" + d);
                if (walk != null) standing.Add(AtNatural(walk));
                if (run != null) standing.Add(AtSpeed(run, WalkSpeed));
            }
            standing.Add(AtSpeed(runF, SprintSpeed, 1.45f));
            PhaseMatch(standing);
        }
        else
        {
            Debug.LogError("[PlayerAnimationBuilder] Locomotion clips missing: import the Mixamo shooter pack or the RPG Character pack.");
        }
        var stand = sm.AddState("Locomotion");
        stand.motion = Directional(ctrl, "Locomotion", standing);
        stand.iKOnFeet = mocap;

        // Crouched and still = kneeling on one knee (when the kneel clip is imported); moving = crouch walk.
        bool canKneel = gen.ContainsKey("Rifle_Kneel");
        var crouch = sm.AddState("Crouch");
        var crouchChildren = new List<Child> { At(canKneel ? gen["Rifle_Kneel"] : gen["Idle"], Vector2.zero) };
        var crouch8 = EightWay.Select(d => RifleLoco("walk crouching " + d)).ToArray();
        if (crouch8.All(c => c != null))
        {
            foreach (var c in crouch8) crouchChildren.Add(AtSpeed(c, CrouchSpeed, 1.45f));
            PhaseMatch(crouchChildren);
        }
        else
        {
            crouchChildren.Add(At(gen["CrouchWalk_F"], new Vector2(0, CrouchSpeed)));
            crouchChildren.Add(At(gen["CrouchWalk_B"], new Vector2(0, -CrouchSpeed)));
            crouchChildren.Add(At(gen["CrouchWalk_L"], new Vector2(-CrouchSpeed, 0)));
            crouchChildren.Add(At(gen["CrouchWalk_R"], new Vector2(CrouchSpeed, 0)));
        }
        crouch.motion = Directional(ctrl, "Crouch", crouchChildren);
        // No foot IK: the kneel (generated, so it has no foot goal curves) would have both feet pulled to the
        // default goal inside the body. The mocap clips share the character's avatar, so they don't need it.
        crouch.iKOnFeet = false;

        // Rifle pack jump (take-off, airborne loop, landing) keeps the rifle carry through the whole jump.
        var jumpClip = RifleLoco("jump up") ?? Shooter("rifle jump") ?? Rpg("Jump", "Unarmed-Jump");
        var landClip = RifleLoco("jump down") ?? Rpg("Jump", "Unarmed-Land");
        var jump = sm.AddState("Jump");
        jump.motion = jumpClip;
        var fall = sm.AddState("Fall");
        fall.motion = RifleLoco("jump loop") ?? Rpg("Fall");
        AnimatorState land = null;
        if (landClip != null)
        {
            land = sm.AddState("Land");
            land.motion = landClip;
            land.iKOnFeet = true;
        }
        sm.defaultState = stand;

        const float stillSpeed = 0.5f;
        if (canKneel)
        {
            // Standing still: kneel down / stand up through the transition clips. Moving: blend straight across.
            var kneelDown = sm.AddState("KneelDown");
            kneelDown.motion = gen["Rifle_KneelDown"];
            var standUp = sm.AddState("StandUp");
            standUp.motion = gen["Rifle_StandUp"];
            Transition(stand, kneelDown, 0.1f, ("Crouch", AnimatorConditionMode.If, 0f), ("Speed", AnimatorConditionMode.Less, stillSpeed));
            Transition(stand, crouch, 0.15f, ("Crouch", AnimatorConditionMode.If, 0f), ("Speed", AnimatorConditionMode.Greater, stillSpeed));
            ExitTo(kneelDown, crouch, 0.1f);
            Transition(kneelDown, crouch, 0.15f, ("Speed", AnimatorConditionMode.Greater, stillSpeed));
            Transition(kneelDown, standUp, 0.1f, ("Crouch", AnimatorConditionMode.IfNot, 0f));
            Transition(crouch, standUp, 0.1f, ("Crouch", AnimatorConditionMode.IfNot, 0f), ("Speed", AnimatorConditionMode.Less, stillSpeed));
            Transition(crouch, stand, 0.15f, ("Crouch", AnimatorConditionMode.IfNot, 0f), ("Speed", AnimatorConditionMode.Greater, stillSpeed));
            ExitTo(standUp, stand, 0.1f);
            Transition(standUp, stand, 0.15f, ("Speed", AnimatorConditionMode.Greater, stillSpeed));
            Transition(standUp, kneelDown, 0.1f, ("Crouch", AnimatorConditionMode.If, 0f));
        }
        else
        {
            Transition(stand, crouch, 0.15f, ("Crouch", AnimatorConditionMode.If, 0f));
            Transition(crouch, stand, 0.15f, ("Crouch", AnimatorConditionMode.IfNot, 0f));
        }
        var toJump = sm.AddAnyStateTransition(jump);
        toJump.duration = 0.08f; toJump.canTransitionToSelf = false; toJump.hasExitTime = false;
        // The rifle pack clips include a wind-up crouch before take-off and an airborne stretch before touch-down;
        // the controller leaves the ground (and lands) instantly, so skip into them.
        if (jumpClip == RifleLoco("jump up")) toJump.offset = 0.25f;
        toJump.AddCondition(AnimatorConditionMode.If, 0, "Jump");
        // Only fall after a moment in the air, so stairs and small drops don't trigger it.
        Transition(stand, fall, 0.15f, ("AirTime", AnimatorConditionMode.Greater, 0.25f));
        Transition(crouch, fall, 0.15f, ("AirTime", AnimatorConditionMode.Greater, 0.25f));
        var jumpToFall = jump.AddTransition(fall);
        jumpToFall.hasExitTime = true; jumpToFall.exitTime = 0.9f; jumpToFall.duration = 0.15f;
        var landing = land ?? stand;
        float landOffset = land != null && landClip == RifleLoco("jump down") ? 0.3f : 0f;
        Transition(jump, landing, 0.08f, ("Grounded", AnimatorConditionMode.If, 0f)).offset = landOffset;
        Transition(fall, landing, 0.08f, ("Grounded", AnimatorConditionMode.If, 0f)).offset = landOffset;
        if (land != null)
        {
            Transition(land, crouch, 0.15f, ("Crouch", AnimatorConditionMode.If, 0f));
            var landToStand = land.AddTransition(stand);
            landToStand.hasExitTime = true; landToStand.exitTime = 0.7f; landToStand.duration = 0.2f;
        }

        // Getting up off the ground after a ragdoll knockdown (Ragdoll.Recover plays these by name).
        // The RPG pack's get-up covers one side; a downloaded Mixamo "getting up" clip is used for the
        // other when present, otherwise the kneel-to-stand.
        var getUp = Rpg("Getup1");
        var mixamoGetUp = ImportedClip(ImportedDir + "Getting Up.fbx");
        bool rpgFaceUp = getUp != null && (measureRig ??= Rig.Create()).StartsFaceUp(getUp);
        AnimationClip faceUpClip = rpgFaceUp ? getUp : mixamoGetUp ?? gen["Rifle_StandUp"];
        AnimationClip faceDownClip = !rpgFaceUp && getUp != null ? getUp : mixamoGetUp ?? gen["Rifle_StandUp"];
        foreach (var (stateName, clip) in new[] { ("GetUpFaceUp", faceUpClip), ("GetUpFaceDown", faceDownClip) })
        {
            var s = sm.AddState(stateName);
            s.motion = clip;
            var back = s.AddTransition(stand);
            back.hasExitTime = true; back.exitTime = 0.92f; back.duration = 0.25f;
        }

        // ----- Masks -----
        var upperMask = UpperBodyMask();

        // ----- Upper body: lowered (Raise = 0) to shouldered (Raise = 1) -----
        // Arms only: the spine and head keep the locomotion's bounce, lean and twist.
        var upper = AddLayer(ctrl, "UpperBody", ArmsMask(), AnimatorLayerBlendingMode.Override);
        var stanceTree = new BlendTree
        {
            name = "Stance",
            blendType = BlendTreeType.Simple1D,
            blendParameter = "Raise",
            useAutomaticThresholds = false,
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(stanceTree, ctrl);
        stanceTree.AddChild(rifleIdle != null ? rifleIdle : gen["Idle"], 0f);
        var aimingIdle = Shooter("rifle aiming idle");
        stanceTree.AddChild(aimingIdle != null ? aimingIdle : gen["Rifle_Aim"], 1f);
        var weaponStance = upper.stateMachine.AddState("Stance");
        weaponStance.motion = stanceTree;
        upper.stateMachine.defaultState = weaponStance;

        // ----- Aim spine: steadies the torso into the shouldered pose while aiming -----
        // Weight is driven from code (PlayerCharacterAnimator) by how raised the rifle is, so lowered and
        // running the spine moves freely, and aimed some of the run's bob still comes through.
        var aimSpine = AddLayer(ctrl, "AimSpine", Mask("Spine", AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head), AnimatorLayerBlendingMode.Override);
        var aimHold = aimSpine.stateMachine.AddState("Aim");
        aimHold.motion = aimingIdle != null ? aimingIdle : gen["Rifle_Aim"];
        aimSpine.stateMachine.defaultState = aimHold;
        var layersCopy = ctrl.layers;
        layersCopy[layersCopy.Length - 1].defaultWeight = 0f;
        ctrl.layers = layersCopy;

        // ----- Actions: additive recoil and reload on top of the aim -----
        var actions = AddLayer(ctrl, "Actions", upperMask, AnimatorLayerBlendingMode.Additive);
        var asm = actions.stateMachine;
        var empty = asm.AddState("Empty");
        asm.defaultState = empty;
        var fire = asm.AddState("Fire");
        // Additive: the recoil relative to the clip's first frame, layered over whatever stance the body is in.
        fire.motion = ImportedClip(FiringShotPath) ?? gen["Fire"];
        var reload = asm.AddState("Reload");
        var reloadClip = Shooter("reloading");
        reload.motion = reloadClip != null ? reloadClip : gen["Reload"];
        if (reloadClip != null) reload.speed = reloadClip.length / ReloadSeconds;
        var hit = asm.AddState("Hit");
        hit.motion = Shooter("hit reaction") ?? gen["Fire"];
        var toHit = asm.AddAnyStateTransition(hit);
        toHit.duration = 0.05f; toHit.canTransitionToSelf = true; toHit.hasExitTime = false;
        toHit.AddCondition(AnimatorConditionMode.If, 0, "Hit");
        var hitBack = hit.AddTransition(empty);
        hitBack.hasExitTime = true; hitBack.exitTime = 1f; hitBack.duration = 0.15f;
        var toFire = asm.AddAnyStateTransition(fire);
        toFire.duration = 0.02f; toFire.canTransitionToSelf = true; toFire.hasExitTime = false;
        toFire.AddCondition(AnimatorConditionMode.If, 0, "Fire");
        var toReload = asm.AddAnyStateTransition(reload);
        toReload.duration = 0.1f; toReload.canTransitionToSelf = false; toReload.hasExitTime = false;
        toReload.AddCondition(AnimatorConditionMode.If, 0, "Reload");
        foreach (var s in new[] { fire, reload })
        {
            var back = s.AddTransition(empty);
            back.hasExitTime = true; back.exitTime = 1f; back.duration = 0.12f;
        }

        // ----- Grip: fingers curled around the pistol grip and handguard, whatever the other layers do -----
        var grip = AddLayer(ctrl, "Grip", FingersMask(), AnimatorLayerBlendingMode.Override);
        var hold = grip.stateMachine.AddState("Hold");
        hold.motion = gen["Idle"];
        grip.stateMachine.defaultState = hold;
        EditorUtility.SetDirty(ctrl);
    }

    static AvatarMask UpperBodyMask() => Mask("UpperBody",
        AvatarMaskBodyPart.Body, AvatarMaskBodyPart.Head, AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm,
        AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers, AvatarMaskBodyPart.LeftHandIK, AvatarMaskBodyPart.RightHandIK);

    static AvatarMask ArmsMask() => Mask("Arms",
        AvatarMaskBodyPart.LeftArm, AvatarMaskBodyPart.RightArm, AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers,
        AvatarMaskBodyPart.LeftHandIK, AvatarMaskBodyPart.RightHandIK);

    static AvatarMask FingersMask() => Mask("Fingers", AvatarMaskBodyPart.LeftFingers, AvatarMaskBodyPart.RightFingers);

    static AvatarMask Mask(string name, params AvatarMaskBodyPart[] active)
    {
        string maskPath = $"{OutDir}/{name}.mask";
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(maskPath);
        if (mask == null) { mask = new AvatarMask(); AssetDatabase.CreateAsset(mask, maskPath); }
        for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
            mask.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, Array.IndexOf(active, (AvatarMaskBodyPart)i) >= 0);
        EditorUtility.SetDirty(mask);
        return mask;
    }

    static AnimatorControllerLayer AddLayer(AnimatorController ctrl, string name, AvatarMask mask, AnimatorLayerBlendingMode mode)
    {
        ctrl.AddLayer(name);
        var layers = ctrl.layers;
        int i = layers.Length - 1;
        layers[i].avatarMask = mask;
        layers[i].defaultWeight = 1f;
        layers[i].blendingMode = mode;
        layers[i].iKPass = true;
        ctrl.layers = layers;
        return ctrl.layers[i];
    }

    static BlendTree Directional(AnimatorController ctrl, string name, List<Child> children)
    {
        var tree = new BlendTree
        {
            name = name,
            blendType = BlendTreeType.FreeformCartesian2D,
            blendParameter = "VelX",
            blendParameterY = "VelZ",
            hideFlags = HideFlags.HideInHierarchy,
        };
        AssetDatabase.AddObjectToAsset(tree, ctrl);
        foreach (var c in children) tree.AddChild(c.motion, c.pos);
        var motions = tree.children;
        for (int i = 0; i < motions.Length; i++)
        {
            motions[i].timeScale = children[i].timeScale;
            motions[i].cycleOffset = children[i].cycleOffset;
        }
        tree.children = motions;
        return tree;
    }

    static void ExitTo(AnimatorState from, AnimatorState to, float duration)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = true;
        t.exitTime = 0.95f;
        t.duration = duration;
    }

    static AnimatorStateTransition Transition(AnimatorState from, AnimatorState to, float duration, params (string param, AnimatorConditionMode mode, float threshold)[] conditions)
    {
        var t = from.AddTransition(to);
        t.hasExitTime = false;
        t.duration = duration;
        foreach (var (param, mode, threshold) in conditions) t.AddCondition(mode, threshold, param);
        return t;
    }
}
